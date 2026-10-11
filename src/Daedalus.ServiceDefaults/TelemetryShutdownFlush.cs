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
///         and the batch is dropped. A host that stops before its first scheduled log export, such as one that fails
///         at startup, would lose every log record. Traces and metrics create their clients eagerly and are not
///         affected; they are flushed here for symmetry.
///     </para>
///     <para>
///         <see cref="IHostedLifecycleService.StoppedAsync"/> runs after every hosted service has stopped, so their
///         shutdown logs are included, and before the container is disposed. The cause is upstream; this stays sound
///         lifecycle practice if it is fixed there.
///     </para>
/// </summary>
internal sealed class TelemetryShutdownFlush(IServiceProvider services) : IHostedLifecycleService
{
    private const int FlushTimeoutMilliseconds = 5000;

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken)
    {
        // Any provider may be absent, so none of them is required.
        (services.GetService(typeof(LoggerProvider)) as LoggerProvider)?.ForceFlush(FlushTimeoutMilliseconds);
        (services.GetService(typeof(TracerProvider)) as TracerProvider)?.ForceFlush(FlushTimeoutMilliseconds);
        (services.GetService(typeof(MeterProvider)) as MeterProvider)?.ForceFlush(FlushTimeoutMilliseconds);
        return Task.CompletedTask;
    }
}
