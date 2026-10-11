using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Daedalus.ServiceDefaults;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Daedalus.Tests.Unit.ServiceDefaults;

/// <summary>
///     Phase 2.9, decisions D2 and D3: the host collects the Thalos, workflow, Mediator and Daedalus sources and
///     meters, and exports traces, metrics and logs over OTLP exactly when a collector endpoint is configured. Each
///     test reads its own provider's in-memory exporter, so other tests' telemetry cannot make it pass.
/// </summary>
public sealed class ServiceDefaultsWiringTests
{
    private static readonly string[] SubscribedSources = ["thalos", "thalos.workflow", "ZeroAlloc.Mediator", "daedalus.manufacture"];
    private static readonly string[] SubscribedMeters = ["thalos", "thalos.workflow", "daedalus.manufacture"];

    /// <summary>
    ///     Red, per source: remove it from <c>TracedSources</c>. For <c>daedalus.manufacture</c>, write <c>"daedalus"</c> in
    ///     place of <c>"daedalus.*"</c>. That source's probe is missing.
    ///     Red: add <c>"*"</c> to <c>TracedSources</c>; the Npgsql probe is exported.
    /// </summary>
    [Fact]
    public void The_tracer_exports_the_thalos_workflow_mediator_and_daedalus_sources_and_not_npgsql()
    {
        var exported = new List<Activity>();
        var services = Services([]);
        services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddInMemoryExporter(exported));
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<TracerProvider>();

        foreach (var name in SubscribedSources.Append("Npgsql"))
        {
            using var source = new ActivitySource(name);
            source.StartActivity($"probe {name}")?.Dispose();
        }

        // Dispose first: the provider appends to the list on any thread until then.
        provider.Dispose();

        exported.Where(a => a.OperationName.StartsWith("probe ", StringComparison.Ordinal)).Select(a => a.Source.Name)
            .Should().BeEquivalentTo(SubscribedSources);
    }

    /// <summary>
    ///     Red, per meter: remove it from <c>Meters</c>; that meter's probe is missing.
    ///     Red: add <c>"*"</c> to <c>Meters</c>; the Npgsql probe is exported.
    /// </summary>
    [Fact]
    public void The_meter_provider_exports_the_thalos_workflow_and_daedalus_meters_and_not_npgsql()
    {
        var exported = new List<Metric>();
        var services = Services([]);
        services.ConfigureOpenTelemetryMeterProvider(metrics => metrics.AddInMemoryExporter(exported));
        using var provider = services.BuildServiceProvider();
        var meterProvider = provider.GetRequiredService<MeterProvider>();

        var meters = SubscribedMeters.Append("Npgsql").Select(name => new Meter(name)).ToList();
        try
        {
            foreach (var meter in meters)
            {
                meter.CreateCounter<long>($"probe.{meter.Name}").Add(1);
            }

            meterProvider.ForceFlush();
        }
        finally
        {
            meters.ForEach(meter => meter.Dispose());
        }

        exported.Select(m => m.Name).Where(n => n.StartsWith("probe.", StringComparison.Ordinal))
            .Should().BeEquivalentTo(SubscribedMeters.Select(name => $"probe.{name}"));
    }

    /// <summary>
    ///     A real host: start, emit one span, one metric and one log, stop at once and dispose. The stop comes before the
    ///     first scheduled log export, so the logs arrive only if the host flushes when it stops.
    ///     Red: drop the <c>UseOtlpExporter()</c> call; the collector receives no request.
    ///     Red: keep only the old logging-only <c>AddOtlpExporter</c>; <c>/v1/traces</c> and <c>/v1/metrics</c> never arrive.
    ///     Red: remove the <c>TelemetryShutdownFlush</c> registration; <c>/v1/logs</c> never arrives, because with
    ///     http/protobuf the log export client is created on the first export, which would fall inside disposal.
    ///     Red: remove <c>logging.AddOpenTelemetry</c> from <c>AddServiceDefaults</c>; <c>/v1/logs</c> never arrives.
    /// </summary>
    [Fact]
    public async Task With_a_collector_endpoint_traces_metrics_and_logs_reach_it_when_the_host_stops()
    {
        using var collector = new FakeOtlpCollector();
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [AspireExtensions.OtlpEndpointKey] = collector.Endpoint,
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
        });
        builder.Services.AddServiceDefaults(builder.Configuration);
        using var host = builder.Build();
        await host.StartAsync();

        using (var source = new ActivitySource("thalos.workflow"))
        {
            source.StartActivity("probe export")?.Dispose();
        }

        using var meter = new Meter("thalos.workflow");
        meter.CreateCounter<long>("probe.export").Add(1);
        host.Services.GetRequiredService<ILogger<ServiceDefaultsWiringTests>>().LogWarning("probe export");
        await host.StopAsync();
        host.Dispose();

        string[] expected = ["/v1/traces", "/v1/metrics", "/v1/logs"];
        await collector.WaitForAsync(expected, TimeSpan.FromSeconds(10));
        collector.Paths.Should().Contain(expected);
    }

    /// <summary>Red: call <c>UseOtlpExporter()</c> whatever the configuration; OTLP registrations appear.</summary>
    [Fact]
    public void Without_a_collector_endpoint_no_otlp_exporter_is_registered()
    {
        var services = Services([]);

        services.Should().NotContain(d => MentionsOtlp(d.ServiceType) || MentionsOtlp(d.ImplementationType));
    }

    /// <summary>
    ///     Red: return true for every request; the health rows fail.
    ///     Red: compare with <c>string.StartsWith("/health")</c>; the <c>/healthy</c> row fails.
    ///     Red: compare case-sensitively; the <c>/HEALTH</c> row fails.
    /// </summary>
    [Theory]
    [InlineData("/health", false)]
    [InlineData("/health/ready", false)]
    [InlineData("/HEALTH", false)]
    [InlineData("/alive", false)]
    [InlineData("/healthy", true)]
    [InlineData("/api/tasks", true)]
    public void Health_and_alive_requests_are_not_traced(string path, bool traced)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;

        AspireExtensions.IsTracedRequest(context).Should().Be(traced);
    }

    private static ServiceCollection Services(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddServiceDefaults(configuration);
        return services;
    }

    private static bool MentionsOtlp(Type? type) =>
        type is not null
        && ((type.FullName?.Contains("Otlp", StringComparison.Ordinal) ?? false) || type.GenericTypeArguments.Any(MentionsOtlp));

    /// <summary>
    ///     A loopback HTTP endpoint that records each request's path and answers 200 with an empty body, which is a valid
    ///     empty OTLP export response. It reads the request line and the headers only, and closes each connection.
    /// </summary>
    private sealed class FakeOtlpCollector : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly ConcurrentQueue<string> _paths = new();
        private readonly CancellationTokenSource _stop = new();

        public FakeOtlpCollector()
        {
            _listener.Start();
            _ = AcceptAsync();
        }

        public string Endpoint => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

        public IReadOnlyCollection<string> Paths => [.. _paths];

        public async Task WaitForAsync(IReadOnlyCollection<string> paths, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (!paths.All(_paths.Contains) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Dispose();
            _stop.Dispose();
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }

                _ = AnswerAsync(client);
            }
        }

        private async Task AnswerAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync();
                if (requestLine?.Split(' ') is [_, var path, ..])
                {
                    _paths.Enqueue(path);
                }

                string? header;
                do
                {
                    header = await reader.ReadLineAsync();
                }
                while (!string.IsNullOrEmpty(header));

                await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: application/x-protobuf\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray());
            }
        }
    }
}
