using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Daedalus.ServiceDefaults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Daedalus.Tests.Unit.ServiceDefaults;

/// <summary>
///     Task B15 fix round 1: <see cref="AspireExtensions.AddServiceDefaults(IServiceCollection, IConfiguration)"/>
///     exports logs over OTLP only when a collector endpoint is configured. Before the fix it always added the OTLP
///     log exporter, so a host run without a collector, which is every Api and Console host outside Aspire and every
///     integration-test host, spent about 4 seconds of every shutdown on the batch processor's final export to an
///     endpoint that was not there.
/// </summary>
public sealed class ServiceDefaultsTelemetryTests
{
    /// <summary>
    ///     Red: adding the OTLP exporter unconditionally, as before, makes the dispose take about 4 seconds while the
    ///     final batch is exported to a collector that is not there.
    /// </summary>
    [Fact]
    public async Task Without_a_collector_endpoint_a_host_disposes_its_logging_without_waiting_on_an_export()
    {
        var elapsed = await LogOnceAndDisposeAsync(new Dictionary<string, string?>(StringComparer.Ordinal));

        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    /// <summary>
    ///     With an endpoint configured, the log is exported there. Red: never adding the OTLP exporter, or inverting the
    ///     condition, leaves the collector with no connection.
    /// </summary>
    [Fact]
    public async Task With_a_collector_endpoint_the_logs_are_exported_to_it()
    {
        using var collector = new TcpListener(IPAddress.Loopback, 0);
        collector.Start();
        var port = ((IPEndPoint)collector.LocalEndpoint).Port;
        Task<TcpClient> connected = collector.AcceptTcpClientAsync();

        await LogOnceAndDisposeAsync(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [AspireExtensions.OtlpEndpointKey] = $"http://127.0.0.1:{port}",
        });

        var accepted = await Task.WhenAny(connected, Task.Delay(TimeSpan.FromSeconds(10)));
        accepted.Should().BeSameAs(connected, "the OTLP exporter must connect to the configured collector");
        (await connected).Dispose();
    }

    private static async Task<TimeSpan> LogOnceAndDisposeAsync(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddServiceDefaults(configuration);

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ILogger<ServiceDefaultsTelemetryTests>>().LogWarning("one line to export");

        var stopwatch = Stopwatch.StartNew();
        await provider.DisposeAsync();
        return stopwatch.Elapsed;
    }
}
