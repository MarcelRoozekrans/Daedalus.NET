using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using Daedalus.Infrastructure.Services.GitHub;
using Microsoft.Extensions.DependencyInjection;

namespace Daedalus.Tests.Unit.Infrastructure.Services.GitHub;

/// <summary>
///     Phase 2.11, decision D5: the remaining quota as a gauge. A listener sees every test's measurements in the process
///     (P26), and the resource tag can only be one of GitHub's documented names, so a test cannot isolate itself with a
///     resource of its own. Each test instead answers with remaining values from a range only it owns, and reads only the
///     measurements inside that range. A test that asserts that nothing is recorded has no value to look for, so it
///     answers with a documented resource that no other test uses and reads that resource.
/// </summary>
public sealed class GitHubRateLimitObserverTests : IDisposable
{
    private readonly long _base = 1_000_000_000L + (RandomNumberGenerator.GetInt32(int.MaxValue) * 1000L);
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

    /// <summary>The remaining value this test owns, <paramref name="offset"/> past its base.</summary>
    private string Value(long offset) => (_base + offset).ToString(CultureInfo.InvariantCulture);

    /// <summary>The measurements inside this test's range, as the offset from its base and the tag.</summary>
    private (long Offset, string? Resource)[] Mine =>
        [.. _measured.Where(m => m.Value >= _base && m.Value < _base + 1000).Select(m => (m.Value - _base, m.Resource))];

    private string?[] Tagged(string resource) =>
        [.. _measured.Where(m => string.Equals(m.Resource, resource, StringComparison.Ordinal)).Select(m => m.Value.ToString(CultureInfo.InvariantCulture))];

    /// <summary>
    ///     Red: record <c>X-RateLimit-Used</c> instead of <c>X-RateLimit-Remaining</c>; the offset is 679.
    ///     Red: tag every measurement <c>core</c>; this answer's resource is <c>search</c>.
    /// </summary>
    [Fact]
    public async Task An_answer_records_the_remaining_quota_tagged_with_its_resource()
    {
        using var invoker = Invoker(_ => Answer(HttpStatusCode.OK, remaining: Value(21), resource: "search", used: Value(679)));

        using var response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user"), CancellationToken.None);

        Mine.Should().Equal((21L, "search"));
    }

    /// <summary>Red: record only a success status; the 304 records nothing.</summary>
    [Fact]
    public async Task A_304_is_recorded_too()
    {
        using var invoker = Invoker(_ => Answer(HttpStatusCode.NotModified, remaining: Value(20), resource: "graphql"));

        using var response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user"), CancellationToken.None);

        Mine.Should().Equal((20L, "graphql"));
    }

    /// <summary>
    ///     A missing quota has no value to find, so it answers with a resource of its own, <c>audit_log_streaming</c>.
    ///     Red: record 0 when <c>X-RateLimit-Remaining</c> is missing; a 0 is then measured under that resource.
    /// </summary>
    [Fact]
    public async Task An_answer_without_the_quota_records_nothing()
    {
        using var invoker = Invoker(_ => Answer(HttpStatusCode.OK, remaining: null, resource: "audit_log_streaming"));

        using var response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user"), CancellationToken.None);

        Tagged("audit_log_streaming").Should().BeEmpty();
    }

    /// <summary>
    ///     The quota is read strictly: digits only. The resource is <c>scim</c>, used by no other test.
    ///     Red: parse with <c>NumberStyles.Integer</c>; <c>-1</c> and <c>" 12"</c> are measured.
    ///     Red: parse with <c>NumberStyles.None | NumberStyles.AllowLeadingSign</c>; <c>-1</c> is measured.
    /// </summary>
    [Theory]
    [InlineData("-1")]
    [InlineData(" 12")]
    [InlineData("abc")]
    [InlineData("99999999999999999999")]
    public async Task A_quota_that_is_not_plain_digits_records_nothing(string remaining)
    {
        using var invoker = Invoker(_ => Answer(HttpStatusCode.OK, remaining: remaining, resource: "scim"));

        using var response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user"), CancellationToken.None);

        Tagged("scim").Should().BeEmpty();
    }

    /// <summary>
    ///     Red: tag a missing resource with an empty string; the pair is then not found.
    /// </summary>
    [Fact]
    public async Task A_missing_resource_is_tagged_unknown()
    {
        using var invoker = Invoker(_ => Answer(HttpStatusCode.OK, remaining: Value(22), resource: null));

        using var response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user"), CancellationToken.None);

        Mine.Should().Equal((22L, "unknown"));
    }

    /// <summary>Red: test <c>string.IsNullOrEmpty</c> instead of <c>IsNullOrWhiteSpace</c>; the blank is tagged <c>other</c>.</summary>
    [Fact]
    public async Task A_blank_resource_is_tagged_unknown()
    {
        using var invoker = Invoker(_ => BlankResource(Value(23)));

        using var response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user"), CancellationToken.None);

        Mine.Should().Equal((23L, "unknown"));
    }

    /// <summary>
    ///     A resource that is not one of GitHub's documented names, in its exact spelling, is tagged <c>other</c>, and
    ///     the raw text appears in no tag, so an upstream cannot make series.
    ///     Red: pass the header through; the tag is the raw text.
    ///     Red: compare the names case-insensitively; <c>CORE</c> is tagged <c>core</c>.
    /// </summary>
    [Theory]
    [InlineData("evil/path?token=x")]
    [InlineData("CORE")]
    public async Task A_resource_that_is_not_documented_is_tagged_other(string resource)
    {
        using var invoker = Invoker(_ => Answer(HttpStatusCode.OK, remaining: Value(24), resource: resource));

        using var response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user"), CancellationToken.None);

        Mine.Should().Equal((24L, "other"));
        _measured.Should().NotContain(m => string.Equals(m.Resource, resource, StringComparison.Ordinal));
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
            Answer(HttpStatusCode.ServiceUnavailable, remaining: Value(Interlocked.Decrement(ref left)), resource: "code_search"));
        await using var host = GitHubHost.Build(primary);

        await host.GetRequiredService<GitHubApi>().GetIssueAsync(RepoRef.Parse("owner/repo").Value, 7);

        Mine.Should().Equal((10L, "code_search"), (9L, "code_search"), (8L, "code_search"), (7L, "code_search"));
    }

    /// <summary>Red: leave the observer off the write client; nothing is measured.</summary>
    [Fact]
    public async Task A_write_is_measured_too()
    {
        var primary = RecordingHandler.Answering(_ => Answer(HttpStatusCode.Created, remaining: Value(30), resource: "integration_manifest"));
        await using var host = GitHubHost.Build(primary);

        await host.GetRequiredService<GitHubApi>().CommentAsync(RepoRef.Parse("owner/repo").Value, 7, "a note");

        Mine.Should().Equal((30L, "integration_manifest"));
    }

    private static HttpMessageInvoker Invoker(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new GitHubRateLimitObserver { InnerHandler = RecordingHandler.Answering(respond) });

    private static HttpResponseMessage BlankResource(string remaining)
    {
        var response = Answer(HttpStatusCode.OK, remaining, resource: null);
        response.Headers.TryAddWithoutValidation("X-RateLimit-Resource", " ");
        return response;
    }

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
