using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using Daedalus.Infrastructure.Services.GitHub;
using Microsoft.Extensions.DependencyInjection;

namespace Daedalus.Tests.Unit.Infrastructure.Services.GitHub;

/// <summary>
///     Phase 2.11, decision D5: the remaining quota as a gauge. Each test tags its answers with a resource only it produces
///     and reads only those measurements, since a listener sees every test's measurements in the process (P26).
/// </summary>
public sealed class GitHubRateLimitObserverTests : IDisposable
{
    private readonly string _resource = "zqx-" + Guid.NewGuid().ToString("N");
    private readonly ConcurrentQueue<(long Value, string? Resource)> _measured = new();
    private readonly MeterListener _listener = new();

    public GitHubRateLimitObserverTests()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (string.Equals(instrument.Meter.Name, GitHubRateLimitObserver.MeterName, StringComparison.Ordinal)
                && string.Equals(instrument.Name, GitHubRateLimitObserver.RemainingName, StringComparison.Ordinal))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? resource = null;
            foreach (var tag in tags)
            {
                if (string.Equals(tag.Key, GitHubRateLimitObserver.ResourceTag, StringComparison.Ordinal))
                    resource = tag.Value as string;
            }

            _measured.Enqueue((value, resource));
        });
        _listener.Start();
    }

    public void Dispose() => _listener.Dispose();

    private long[] Mine => [.. _measured.Where(m => string.Equals(m.Resource, _resource, StringComparison.Ordinal)).Select(m => m.Value)];

    /// <summary>
    ///     Red: record <c>X-RateLimit-Used</c> instead of <c>X-RateLimit-Remaining</c>; the value is 679.
    ///     Red: tag every measurement <c>core</c>; this resource's measurements are missing.
    /// </summary>
    [Fact]
    public async Task An_answer_records_the_remaining_quota_tagged_with_its_resource()
    {
        using var invoker = Invoker(_ => Answer(HttpStatusCode.OK, remaining: "4321", resource: _resource, used: "679"));

        using var response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user"), CancellationToken.None);

        Mine.Should().Equal(4321);
    }

    /// <summary>Red: record only a success status; the 304 records nothing.</summary>
    [Fact]
    public async Task A_304_is_recorded_too()
    {
        using var invoker = Invoker(_ => Answer(HttpStatusCode.NotModified, remaining: "4320", resource: _resource));

        using var response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user"), CancellationToken.None);

        Mine.Should().Equal(4320);
    }

    /// <summary>Red: record 0 when <c>X-RateLimit-Remaining</c> is missing; a 0 is then measured.</summary>
    [Fact]
    public async Task An_answer_without_the_quota_records_nothing()
    {
        using var invoker = Invoker(_ => Answer(HttpStatusCode.OK, remaining: null, resource: _resource));

        using var response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user"), CancellationToken.None);

        Mine.Should().BeEmpty();
    }

    /// <summary>
    ///     The value is unique to this test, since the tag cannot be.
    ///     Red: tag a missing resource with an empty string; the pair is then not found.
    /// </summary>
    [Fact]
    public async Task A_missing_resource_is_tagged_unknown()
    {
        using var invoker = Invoker(_ => Answer(HttpStatusCode.OK, remaining: "987654321", resource: null));

        using var response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user"), CancellationToken.None);

        _measured.Should().Contain((987_654_321L, "unknown"));
    }

    /// <summary>
    ///     The observer sits inside the retries, so every attempt is measured.
    ///     Red: add the observer to the read client before the resilience handler; one measurement, the last.
    ///     Red: leave the observer off the read client; none.
    /// </summary>
    [Fact]
    public async Task Every_attempt_of_a_retried_read_is_measured()
    {
        var left = 11;
        var primary = RecordingHandler.Answering(_ =>
            Answer(HttpStatusCode.ServiceUnavailable, remaining: Interlocked.Decrement(ref left).ToString(System.Globalization.CultureInfo.InvariantCulture), resource: _resource));
        await using var host = GitHubHost.Build(primary);

        await host.GetRequiredService<GitHubApi>().GetIssueAsync(RepoRef.Parse("owner/repo").Value, 7);

        Mine.Should().Equal(10, 9, 8, 7);
    }

    /// <summary>Red: leave the observer off the write client; nothing is measured.</summary>
    [Fact]
    public async Task A_write_is_measured_too()
    {
        var primary = RecordingHandler.Answering(_ => Answer(HttpStatusCode.Created, remaining: "4000", resource: _resource));
        await using var host = GitHubHost.Build(primary);

        await host.GetRequiredService<GitHubApi>().CommentAsync(RepoRef.Parse("owner/repo").Value, 7, "a note");

        Mine.Should().Equal(4000);
    }

    private static HttpMessageInvoker Invoker(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new GitHubRateLimitObserver { InnerHandler = RecordingHandler.Answering(respond) });

    private static HttpResponseMessage Answer(HttpStatusCode status, string? remaining, string? resource, string? used = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent("{}") };
        if (remaining is not null)
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", remaining);
        if (resource is not null)
            response.Headers.TryAddWithoutValidation("X-RateLimit-Resource", resource);
        if (used is not null)
            response.Headers.TryAddWithoutValidation("X-RateLimit-Used", used);
        return response;
    }
}
