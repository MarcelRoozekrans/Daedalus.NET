using System.Diagnostics;
using System.Diagnostics.Metrics;
using Daedalus.ServiceDefaults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Daedalus.Tests.Unit.ServiceDefaults;

/// <summary>
///     The stop-time flush shares one budget across the three providers, so a collector that never answers cannot add
///     one full timeout per provider to a host's shutdown.
/// </summary>
public sealed class TelemetryShutdownFlushTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(300);

    /// <summary>
    ///     Flushes that block for the time they are given, as an exporter against a collector that never answers does.
    ///     The first returns after 100 ms, the second blocks for what is left, so the third gets no time.
    ///     Red: give each flush the whole budget instead of what is left; the total is far over the budget.
    ///     Red: drop the remaining-budget check; the third flush is called.
    ///     Red: pass the flushes in another order; the order assertion fails.
    /// </summary>
    [Fact]
    public void One_budget_is_shared_by_all_flushes_in_order()
    {
        var calls = new List<(string Name, int Timeout)>();
        Func<int, bool> Blocking(string name, int cap) => timeout =>
        {
            calls.Add((name, timeout));
            Thread.Sleep(Math.Min(timeout, cap));
            return false;
        };

        var clock = Stopwatch.StartNew();
        TelemetryShutdownFlush.FlushWithinBudget(
            [Blocking("logs", 100), Blocking("traces", int.MaxValue), Blocking("metrics", int.MaxValue)],
            Budget,
            CancellationToken.None);
        clock.Stop();

        calls.Select(c => c.Name).Should().Equal("logs", "traces");
        calls[0].Timeout.Should().BeLessThanOrEqualTo((int)Budget.TotalMilliseconds);
        calls[1].Timeout.Should().BeInRange(1, (int)Budget.TotalMilliseconds - 90);
        clock.Elapsed.Should().BeLessThan(Budget + TimeSpan.FromMilliseconds(250));
    }

    /// <summary>Red: ignore the token; the flush runs although the stop was cancelled.</summary>
    [Fact]
    public async Task A_cancelled_stop_flushes_nothing()
    {
        var called = false;
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        TelemetryShutdownFlush.FlushWithinBudget(
            [timeout =>
            {
                called = true;
                return true;
            }],
            Budget,
            cancelled.Token);

        called.Should().BeFalse();
    }

    /// <summary>Red: resolve a provider with GetRequiredService; a missing one throws.</summary>
    [Fact]
    public async Task A_stop_without_providers_does_nothing()
    {
        var flush = new TelemetryShutdownFlush(new ServiceCollection().BuildServiceProvider(), TelemetryShutdownFlushOptions.Default);

        var act = () => flush.StoppedAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    /// <summary>
    ///     Real providers, each holding pending telemetry that only a flush delivers.
    ///     Red: leave the logger provider out of the flush; no log record is flushed.
    ///     Red: leave the tracer provider out of the flush; no span is flushed.
    ///     Red: leave the meter provider out of the flush; no metric is flushed.
    /// </summary>
    [Fact]
    public async Task A_stop_flushes_the_logger_tracer_and_meter_providers()
    {
        var logs = new List<LogRecord>();
        var spans = new List<Activity>();
        var metrics = new List<Metric>();
        using var source = new ActivitySource("zqx.flush.probe");
        using var meter = new Meter("zqx.flush.probe");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOpenTelemetry()
            .WithLogging(
                logging => logging.AddProcessor(
                    new BatchLogRecordExportProcessor(new InMemoryExporter<LogRecord>(logs), scheduledDelayMilliseconds: 600_000)),
                options => { })
            .WithTracing(tracing => tracing
                .AddSource(source.Name)
                .AddProcessor(new BatchActivityExportProcessor(new InMemoryExporter<Activity>(spans), scheduledDelayMilliseconds: 600_000)))
            .WithMetrics(metering => metering
                .AddMeter(meter.Name)
                .AddReader(new PeriodicExportingMetricReader(new InMemoryExporter<Metric>(metrics), 600_000)));
        services.AddSingleton<TelemetryShutdownFlush>();
        services.AddSingleton(TelemetryShutdownFlushOptions.Default);
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<TracerProvider>();
        provider.GetRequiredService<MeterProvider>();
        provider.GetRequiredService<ILogger<TelemetryShutdownFlushTests>>().LogWarning("probe");
        source.StartActivity("probe")?.Dispose();
        meter.CreateCounter<long>("zqx.flush.counter").Add(1);

        await provider.GetRequiredService<TelemetryShutdownFlush>().StoppedAsync(CancellationToken.None);

        logs.Should().NotBeEmpty();
        spans.Should().NotBeEmpty();
        metrics.Should().NotBeEmpty();
    }

    /// <summary>Red: register it with AddSingleton and call twice; two registrations appear.</summary>
    [Fact]
    public void Adding_the_service_defaults_twice_registers_one_flush()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [AspireExtensions.OtlpEndpointKey] = "http://127.0.0.1:1",
            }).Build();

        services.AddServiceDefaults(configuration);
        services.AddServiceDefaults(configuration);

        services.Count(d => d.ImplementationType == typeof(TelemetryShutdownFlush)).Should().Be(1);
    }
}
