using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Thalos.Workflow;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     Runs <see cref="ProcessDefinitionSync.SyncAsync"/> once at boot, before <see cref="WorkflowOutboxDispatchService"/>
///     can dispatch a run's first node — per <c>docs/workflow.md</c>, "Call <c>SyncAsync</c> at startup, before
///     anything starts a run". A process is only runnable once its YAML is in <c>process_definition</c>; without
///     this, <see cref="FileSystemProcessDefinitionSource"/> is registered but never read.
/// </summary>
/// <remarks>
///     <para>
///     <b>What the ordering claim rests on.</b> Not "before the host accepts work": <c>GenericWebHostService</c> is
///     registered by <c>WebApplicationBuilder</c>'s own constructor, long before <c>Program.cs</c> calls
///     <c>AddDaedalusAgents</c>, so Kestrel is already listening by the time this runs. What does hold is
///     sync-before-dispatch, and the only thing enforcing it is registration order:
///     <c>DaedalusAgentsServiceCollectionExtensions.AddDaedalusWorkflow</c> registers this service before
///     <see cref="WorkflowOutboxDispatchService"/>, and <c>IHost.StartAsync</c> starts <see cref="IHostedService"/>s
///     in registration order. Moving either registration past the other silently reopens the window in which a run
///     can be dispatched against a definition that has not synced yet.
///     </para>
///     <para>
///     <b>Does not fail the host on a bad process file.</b> <see cref="ProcessDefinitionSync.SyncAsync"/> is
///     designed to degrade per-document: a document that fails to load or validate is reported and left alone, and
///     the version already active for that process (if any) keeps running unchanged — see that method's own
///     remarks. Treating a sync failure as fatal here would turn one contributor's bad YAML edit into a boot
///     failure for every process, including ones the edit never touched.
///     </para>
///     <para>
///     <b>Does not fail the host on a store-side exception either.</b> That degradation is a <em>Result</em>
///     contract, and nothing upholds it below <see cref="ProcessDefinitionSync"/>: <c>OrmProcessDefinitionStore</c>
///     has no catch anywhere, so a database that is reachable but missing <c>process_definition</c> throws an
///     <c>NpgsqlException</c> straight out of <see cref="ProcessDefinitionSync.SyncAsync"/> and out of
///     <see cref="StartAsync"/>, which nothing above catches — crash-looping the whole API, every non-workflow
///     endpoint included, for a subsystem no endpoint depends on yet. The catch below is the same shape
///     <see cref="WorkflowStrandedRunSweepService"/> and <see cref="WorkflowOutboxDispatchService"/> already use,
///     and for the same reason. A catch inside Thalos's own store remains the deeper fix and stays a
///     carried-forward item in <c>docs/planning/STATE.md</c>.
///     </para>
/// </remarks>
internal sealed partial class ProcessDefinitionSyncHostedService(
    ProcessDefinitionSync sync,
    ILogger<ProcessDefinitionSyncHostedService> logger) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
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
        // A host shutdown during startup cancels cancellationToken; that is not a sync failure and must
        // propagate so the host stops promptly instead of logging a spurious error and carrying on.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSyncThrew(logger, ex);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 1830, Level = LogLevel.Information, Message = "Synced {Count} process definition(s).")]
    private static partial void LogSynced(ILogger logger, int count);

    [LoggerMessage(EventId = 1831, Level = LogLevel.Error, Message = "Process definition sync reported errors: {Error}")]
    private static partial void LogSyncReportedErrors(ILogger logger, string error);

    [LoggerMessage(EventId = 1832, Level = LogLevel.Error, Message = "Process definition sync threw; no process definition was synced and the workflow engine will have nothing to run. The rest of the host started normally.")]
    private static partial void LogSyncThrew(ILogger logger, Exception exception);
}
