using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
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
///     <para>
///         What an exporter sees is checked on a copy taken at export time, not on the stored <see cref="Activity"/>: the
///         processor mutates the same object, so a stored reference passes whether the cut happened before or after the
///         export. Ordering guarantee: processors of weight 0 run in registration order, and the OTLP exporter is
///         ordered last by its own pipeline weight. A hand-added exporter must be registered after
///         <c>AddServiceDefaults</c>, as <c>Host</c> does.
///     </para>
/// </summary>
public sealed class HttpClientSpanRedactionTests
{
    private const string Path = "/bot123456:SECRET-token/getMe";
    private const string Query = "x=1";

    /// <summary>
    ///     Red: removing <c>AddProcessor</c> from <c>AddServiceDefaults</c> exports the runtime's <c>url.full</c>, which
    ///     keeps the path, so the origin assertion and the leak assertion both fail. Red: registering the exporter before
    ///     <c>AddServiceDefaults</c> in <c>Host</c> copies the span before the cut, so the copied <c>url.full</c> keeps
    ///     the path.
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
        span.Tags["url.full"].Should().Be($"http://127.0.0.1:{port}");
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
        span.Tags["url.full"].Should().Be($"http://127.0.0.1:{port}");
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

    private static void AssertNoPathAnywhere(ExportedSpan span)
    {
        var everything = new List<string?> { span.DisplayName };
        everything.AddRange(span.Tags.Keys);
        everything.AddRange(span.Tags.Values);
        everything.Where(v => v is not null).Should().NotContain(
            v => v!.Contains("SECRET-token", StringComparison.Ordinal)
                || v.Contains("bot123456", StringComparison.Ordinal)
                || v.Contains("getMe", StringComparison.Ordinal)
                || v.Contains("x=1", StringComparison.Ordinal));
    }

    /// <summary>The exporter is read through one snapshot, filtered to this test's port: other tests share the process.</summary>
    private static List<ExportedSpan> HttpSpansTo(ServiceProvider host, CopyingExporter exported, int port)
    {
        host.GetRequiredService<TracerProvider>().ForceFlush().Should().BeTrue();
        return exported.Snapshot()
            .Where(a => string.Equals(a.Source, "System.Net.Http", StringComparison.Ordinal)
                && a.Tags.TryGetValue("server.port", out var p)
                && string.Equals(p, port.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>
    ///     The exporter is registered after <c>AddServiceDefaults</c>, so it runs after the redaction processor, as every
    ///     hand-added exporter must.
    /// </summary>
    private static ServiceProvider Host(out CopyingExporter exported)
    {
        var configuration = new ConfigurationBuilder().Build();
        var copying = new CopyingExporter();
        exported = copying;
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddServiceDefaults(configuration);
        services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddProcessor(new SimpleActivityExportProcessor(copying)));
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

    /// <summary>What an exporter saw of one span at the moment it was exported.</summary>
    private sealed record ExportedSpan(
        string Source, string DisplayName, ActivityStatusCode Status, Dictionary<string, string?> Tags);

    /// <summary>Copies each span's name and tags when it is exported, so later changes to the Activity do not show.</summary>
    private sealed class CopyingExporter : BaseExporter<Activity>
    {
        private readonly Lock _gate = new();
        private readonly List<ExportedSpan> _spans = [];

        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch)
            {
                var tags = activity.TagObjects.ToDictionary(
                    t => t.Key, t => t.Value?.ToString(), StringComparer.Ordinal);
                foreach (var e in activity.Events)
                {
                    foreach (var t in e.Tags)
                    {
                        tags[$"event:{e.Name}:{t.Key}"] = t.Value?.ToString();
                    }
                }

                var copy = new ExportedSpan(activity.Source.Name, activity.DisplayName, activity.Status, tags);
                lock (_gate)
                {
                    _spans.Add(copy);
                }
            }

            return ExportResult.Success;
        }

        public List<ExportedSpan> Snapshot()
        {
            lock (_gate)
            {
                return [.. _spans];
            }
        }
    }
}
