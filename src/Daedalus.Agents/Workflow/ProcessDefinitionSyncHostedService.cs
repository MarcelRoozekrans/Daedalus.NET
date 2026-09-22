using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Thalos.Workflow;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     Runs <see cref="ProcessDefinitionSync.SyncAsync"/> once at boot, before the host accepts work — per
///     <c>docs/workflow.md</c>, "Call <c>SyncAsync</c> at startup, before anything starts a run". A process is
///     only runnable once its YAML is in <c>process_definition</c>; without this, <see cref="GitProcessDefinitionSource"/>
///     is registered but never read.
/// </summary>
/// <remarks>
///     Does not fail the host on a bad process file. <see cref="ProcessDefinitionSync.SyncAsync"/> is designed to
///     degrade per-document: a document that fails to load or validate is reported and left alone, and the
///     version already active for that process (if any) keeps running unchanged — see that method's own remarks.
///     Treating a sync failure as fatal here would turn one contributor's bad YAML edit into a boot failure for
///     every process, including ones the edit never touched.
/// </remarks>
internal sealed partial class ProcessDefinitionSyncHostedService(
    ProcessDefinitionSync sync,
    ILogger<ProcessDefinitionSyncHostedService> logger) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var result = await sync.SyncAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            LogSyncReportedErrors(logger, result.Error);
        }
        else if (result.Value > 0)
        {
            LogSynced(logger, result.Value);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 1830, Level = LogLevel.Information, Message = "Synced {Count} process definition(s).")]
    private static partial void LogSynced(ILogger logger, int count);

    [LoggerMessage(EventId = 1831, Level = LogLevel.Error, Message = "Process definition sync reported errors: {Error}")]
    private static partial void LogSyncReportedErrors(ILogger logger, string error);
}
