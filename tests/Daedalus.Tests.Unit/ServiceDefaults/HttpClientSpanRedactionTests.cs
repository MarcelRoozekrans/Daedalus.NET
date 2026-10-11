using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Daedalus.ServiceDefaults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace Daedalus.Tests.Unit.ServiceDefaults;

/// <summary>
///     An outgoing HttpClient span exports the origin of its call and nothing of the path. The Telegram channel puts the
///     bot token in the path, <c>/bot{token}/{method}</c>, and calls through a plain <c>new HttpClient</c>, so the tests
///     do the same, against a loopback listener, through a provider built by <c>AddServiceDefaults</c>.
/// </summary>
public sealed class HttpClientSpanRedactionTests
{
    private const string Path = "/bot123456:SECRET-token/getMe";
    private const string Query = "x=1";

    /// <summary>
    ///     Red: removing <c>AddProcessor</c> from <c>AddServiceDefaults</c> exports the runtime's <c>url.full</c>, which
    ///     keeps the path, so the origin assertion and the leak assertion both fail.
    /// </summary>
    [Fact]
    public async Task A_successful_call_exports_only_its_origin()
    {
        using var listener = StartListener(out var port);
        using var host = Host(out var exported);

        using (var client = new HttpClient())
        {
            (await client.GetAsync(new Uri($"http://127.0.0.1:{port}{Path}?{Query}"))).EnsureSuccessStatusCode();
        }

        var span = HttpSpansTo(host, exported, port).Should().ContainSingle().Subject;
        span.GetTagItem("url.full").Should().Be($"http://127.0.0.1:{port}");
        AssertNoPathAnywhere(span);
    }

    /// <summary>
    ///     A call that fails to connect ends its span on the exception path. Red: the same removal leaves the path in
    ///     <c>url.full</c> of the failed span too.
    /// </summary>
    [Fact]
    public async Task A_failed_call_exports_only_its_origin()
    {
        var port = UnusedPort();
        using var host = Host(out var exported);

        using (var client = new HttpClient())
        {
            var act = () => client.GetAsync(new Uri($"http://127.0.0.1:{port}{Path}?{Query}"));
            await act.Should().ThrowAsync<HttpRequestException>();
        }

        var span = HttpSpansTo(host, exported, port).Should().ContainSingle().Subject;
        span.Status.Should().Be(ActivityStatusCode.Error);
        span.GetTagItem("url.full").Should().Be($"http://127.0.0.1:{port}");
        AssertNoPathAnywhere(span);
    }

    /// <summary>Red: returning the input, or building the origin from <c>GetLeftPart</c> of the authority, keeps the userinfo or path.</summary>
    [Theory]
    [MemberData(nameof(Origins))]
    public void The_origin_has_no_path_query_or_userinfo(Uri url, string expected)
    {
        HttpClientOriginOnlyProcessor.OriginOf(url.OriginalString).Should().Be(expected);
    }

    public static TheoryData<Uri, string> Origins => new()
    {
        { new Uri("https://api.telegram.org/bot1:T/getMe?x=1"), "https://api.telegram.org" },
        { new Uri("https://user:pw@example.com:8443/a/b"), "https://example.com:8443" },
        { new Uri("http://[::1]:5000/x"), "http://[::1]:5000" },
    };

    private static void AssertNoPathAnywhere(Activity span)
    {
        var everything = new List<string?> { span.DisplayName, span.OperationName };
        everything.AddRange(span.TagObjects.Select(t => t.Key));
        everything.AddRange(span.TagObjects.Select(t => t.Value?.ToString()));
        everything.AddRange(span.Events.SelectMany(e => e.Tags.Select(t => t.Value?.ToString())));
        everything.AddRange(span.Events.Select(e => e.Name));
        everything.Where(v => v is not null).Should().NotContain(
            v => v!.Contains("SECRET-token", StringComparison.Ordinal)
                || v.Contains("bot123456", StringComparison.Ordinal)
                || v.Contains("getMe", StringComparison.Ordinal)
                || v.Contains("x=1", StringComparison.Ordinal));
    }

    /// <summary>The exporter is read through one snapshot, filtered to this test's port: other tests share the process.</summary>
    private static List<Activity> HttpSpansTo(ServiceProvider host, LockedActivities exported, int port)
    {
        host.GetRequiredService<TracerProvider>().ForceFlush().Should().BeTrue();
        return exported.Snapshot()
            .Where(a => string.Equals(a.Source.Name, "System.Net.Http", StringComparison.Ordinal)
                && Equals(a.GetTagItem("server.port"), port))
            .ToList();
    }

    private static ServiceProvider Host(out LockedActivities exported)
    {
        var configuration = new ConfigurationBuilder().Build();
        var activities = new LockedActivities();
        exported = activities;
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddServiceDefaults(configuration);
        services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddInMemoryExporter((ICollection<Activity>)activities));
        var provider = services.BuildServiceProvider();

        // The provider subscribes to the sources when it is built, so build it before any call is made.
        provider.GetRequiredService<TracerProvider>();
        return provider;
    }

    private static HttpListener StartListener(out int port)
    {
        port = UnusedPort();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var context = await listener.GetContextAsync();
                    context.Response.StatusCode = 200;
                    context.Response.Close();
                }
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                // The listener was closed by the test.
            }
        });
        return listener;
    }

    private static int UnusedPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    /// <summary>The in-memory exporter adds without a lock, on whichever thread stops a span.</summary>
    private sealed class LockedActivities : Collection<Activity>
    {
        private readonly Lock _gate = new();

        protected override void InsertItem(int index, Activity item)
        {
            lock (_gate)
            {
                base.InsertItem(index, item);
            }
        }

        public List<Activity> Snapshot()
        {
            lock (_gate)
            {
                return [.. this];
            }
        }
    }
}
