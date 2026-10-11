using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Daedalus.ServiceDefaults;

/// <summary>
///     Flushes the logger, tracer and meter providers once the host has stopped, while its container is still alive.
///     <para>
///         Why it exists: with <c>OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf</c>, OpenTelemetry 1.17.0 creates the OTLP
///         log exporter's HTTP client lazily, on the first export, by resolving it from the root service provider
///         (opentelemetry-dotnet PR 7298). The only final flush is inside <c>LoggerProvider.Dispose</c>, which runs
///         while the container is already disposing, so that resolution throws <see cref="ObjectDisposedException"/>
///         and the batch is dropped. A host that stops normally before its first scheduled log export would lose every
///         log record. Traces and metrics create their clients eagerly and are not affected; they are flushed here for
///         symmetry.
///     </para>
///     <para>
///         Not covered: in .NET 10 a host whose <c>StartAsync</c> fails is disposed without <c>StoppedAsync</c> running,
///         so its unexported logs, startup failures included, are still lost until the upstream fix (issue #332).
///     </para>
///     <para>
///         <see cref="IHostedLifecycleService.StoppedAsync"/> runs after every hosted service has stopped, so their
///         shutdown logs are included, and before the container is disposed. The cause is upstream; this stays sound
///         lifecycle practice if it is fixed there.
///     </para>
/// </summary>
internal sealed class TelemetryShutdownFlush(IServiceProvider services, TelemetryShutdownFlushOptions options)
    : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    ///     Flushes logs first, then traces, then metrics, within one <see cref="TelemetryShutdownFlushOptions.Budget"/>.
    ///     Any provider may be absent, so none is required.
    /// </summary>
    public Task StoppedAsync(CancellationToken cancellationToken)
    {
        Func<int, bool>?[] flushes =
        [
            (services.GetService(typeof(LoggerProvider)) as LoggerProvider) is { } logs ? logs.ForceFlush : null,
            (services.GetService(typeof(TracerProvider)) as TracerProvider) is { } traces ? traces.ForceFlush : null,
            (services.GetService(typeof(MeterProvider)) as MeterProvider) is { } metrics ? metrics.ForceFlush : null,
        ];
        FlushWithinBudget(flushes, options.Budget, cancellationToken);
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Runs the flushes in order, each given what is left of <paramref name="budget"/> in milliseconds. Once the
    ///     budget is spent or <paramref name="cancellationToken"/> is cancelled, the rest are skipped.
    /// </summary>
    internal static void FlushWithinBudget(
        IEnumerable<Func<int, bool>?> flushes, TimeSpan budget, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        foreach (var flush in flushes)
        {
            var remaining = budget - clock.Elapsed;
            if (cancellationToken.IsCancellationRequested || remaining <= TimeSpan.Zero)
            {
                break;
            }

            flush?.Invoke((int)Math.Ceiling(remaining.TotalMilliseconds));
        }
    }
}

/// <summary>The time <see cref="TelemetryShutdownFlush"/> may spend flushing, in total, on one stop.</summary>
internal sealed record TelemetryShutdownFlushOptions(TimeSpan Budget)
{
    /// <summary>The budget used by the host.</summary>
    public static TelemetryShutdownFlushOptions Default { get; } = new(TimeSpan.FromSeconds(5));
}
