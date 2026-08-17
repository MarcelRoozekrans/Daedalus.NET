using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Thalos.Memory;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Agents.Memory;

/// <summary>
///     Embeds memories whose vector is missing (<c>IndexPending</c>): rows written while Ollama was down, rows migrated from
///     <c>StructuredLearnings</c>, rows written by the Ralph console host. Runs at startup (after a short delay so the Rag.NET
///     schema step is done) and then periodically — every <see cref="ReindexConfig.RetryInterval"/> while the index is
///     unavailable or rows failed, every <see cref="ReindexConfig.SweepInterval"/> otherwise. Never fails host start.
/// </summary>
/// <remarks>
///     Registered by <c>AddDaedalusAgents</c> only (the API host), never by <c>AddDaedalusMemory</c>: one sweeper per database
///     is enough and the API is also the host that owns the Rag.NET schema.
/// </remarks>
internal sealed partial class ReindexPendingMemoriesHostedService(
    IMemoryService memory,
    IMemoryIndex index,
    MemoryConfig config,
    TimeProvider clock,
    ILogger<ReindexPendingMemoriesHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(config.Reindex.StartupDelay, clock, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                var wait = await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(wait, clock, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // host shutting down
        }
    }

    /// <summary>One probe + reindex pass; returns how long to wait before the next one.</summary>
    internal async Task<TimeSpan> RunOnceAsync(CancellationToken ct)
    {
        try
        {
            var health = await index.ProbeAsync(ct).ConfigureAwait(false);
            if (health.IsFailure || !health.Value.Available)
            {
                LogIndexUnavailable(logger, health.IsFailure ? health.Error.Code.ToString() : health.Value.Detail ?? "unknown");
                return config.Reindex.RetryInterval;
            }

            var report = await memory.ReindexAsync(new ReindexOptions { PendingOnly = true }, ct).ConfigureAwait(false);
            if (report.IsFailure)
            {
                LogReindexFailed(logger, report.Error.Code.ToString(), report.Error.Message);
                return config.Reindex.RetryInterval;
            }

            if (report.Value.Scanned > 0)
            {
                LogReindexed(logger, report.Value.Indexed, report.Value.Failed);
            }

            return report.Value.Failed > 0 ? config.Reindex.RetryInterval : config.Reindex.SweepInterval;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogReindexFailed(logger, ex.GetType().Name, "unexpected exception");
            return config.Reindex.RetryInterval;
        }
    }

    [LoggerMessage(EventId = 410, Level = LogLevel.Information, Message = "Memory index unavailable ({Reason}); pending memories stay index_pending until the next attempt")]
    private static partial void LogIndexUnavailable(ILogger logger, string reason);

    [LoggerMessage(EventId = 411, Level = LogLevel.Information, Message = "Reindexed pending memories: {Indexed} indexed, {Failed} failed")]
    private static partial void LogReindexed(ILogger logger, int indexed, int failed);

    [LoggerMessage(EventId = 412, Level = LogLevel.Warning, Message = "Reindexing pending memories failed: {Code} {Message}")]
    private static partial void LogReindexFailed(ILogger logger, string code, string message);
}
