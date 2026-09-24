using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Thalos;
using Thalos.Skills;
using Thalos.Skills.Charters;
using Thalos.Workflow;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Agents;

/// <summary>
///     Periodically re-runs the skill, role-charter and process-definition syncs so a content edit on disk (a
///     <c>SKILL.md</c>, a <c>roles/*.md</c>, a <c>processes/*.yaml</c>) reaches a running host without a
///     redeploy. Off by default — see <see cref="ContentConfig.ResyncInterval"/> — and only registered by
///     <see cref="DaedalusAgentsServiceCollectionExtensions.AddDaedalusAgents(Microsoft.Extensions.DependencyInjection.IServiceCollection, DaedalusAgentsOptions, Microsoft.Extensions.Configuration.IConfiguration, Microsoft.Extensions.Hosting.IHostEnvironment, Microsoft.Extensions.AI.IEmbeddingGenerator{string, Microsoft.Extensions.AI.Embedding{float}}?)"/>
///     when that interval is set — see that method's own remarks for why <see cref="SkillSyncService"/> and
///     <see cref="CharterSyncService"/> must be forwarded to a concrete singleton before this type can resolve
///     the same instance the host's <see cref="IHostedService"/> pipeline runs.
/// </summary>
/// <remarks>
///     <para>
///     <b>Each sync is isolated.</b> The three calls on one tick each run inside their own
///     <c>try</c>/<c>catch</c>: a failure — a returned <c>Result</c> failure, or a thrown exception — is
///     logged and neither stops the other two syncs on the same tick nor
///     stops the loop. This is the same "a tick must never let an exception escape" rule
///     <see cref="Workflow.WorkflowOutboxDispatchService"/> and <see cref="Scheduling.ScheduleSweeperService"/>
///     already document for their own loops; the difference here is that one tick fans out to three independent
///     units of work rather than one, so each needs its own boundary — a single shared <c>try</c>/<c>catch</c>
///     around all three would let a throwing skill sync starve the charter and process syncs of their turn.
///     </para>
///     <para>
///     <see cref="ProcessDefinitionSync"/> is only ever registered when <c>Thalos:Workflow:Enabled</c> is true
///     (see <see cref="WorkflowConfig.Enabled"/>'s remarks) — when it is not, the constructor receives
///     <see langword="null"/> and that tick's process step is skipped; the skill and charter syncs still run.
///     </para>
///     <para>
///     Uses the <see cref="PeriodicTimer(TimeSpan, TimeProvider)"/> overload so a test can drive every tick
///     through a fake <see cref="TimeProvider"/> instead of wall-clock time.
///     </para>
/// </remarks>
internal sealed partial class ContentResyncService(
    TimeSpan interval,
    SkillSyncService skillSync,
    CharterSyncService charterSync,
    ProcessDefinitionSync? processSync,
    TimeProvider clock,
    ILogger<ContentResyncService> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval, clock);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await RunSkillSyncAsync(stoppingToken).ConfigureAwait(false);
                await RunCharterSyncAsync(stoppingToken).ConfigureAwait(false);
                await RunProcessSyncAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // host shutting down — not a sync failure, nothing to log
        }
    }

    private async Task RunSkillSyncAsync(CancellationToken ct)
    {
        try
        {
            var result = await skillSync.SyncAsync(ct).ConfigureAwait(false);
            if (result.IsFailure)
            {
                LogSkillSyncFailed(logger, result.Error.Code.ToString(), result.Error.Message);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSkillSyncThrew(logger, ex);
        }
    }

    private async Task RunCharterSyncAsync(CancellationToken ct)
    {
        try
        {
            var result = await charterSync.SyncAsync(ct).ConfigureAwait(false);
            if (result.IsFailure)
            {
                LogCharterSyncFailed(logger, result.Error.Code.ToString(), result.Error.Message);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCharterSyncThrew(logger, ex);
        }
    }

    private async Task RunProcessSyncAsync(CancellationToken ct)
    {
        if (processSync is null)
        {
            // Thalos:Workflow:Enabled is false on this host — nothing registered ProcessDefinitionSync, and the
            // skill/charter syncs above already ran, so this tick simply has one fewer step.
            return;
        }

        try
        {
            var result = await processSync.SyncAsync(ct).ConfigureAwait(false);
            if (result.IsFailure)
            {
                LogProcessSyncFailed(logger, result.Error);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogProcessSyncThrew(logger, ex);
        }
    }

    [LoggerMessage(EventId = 1840, Level = LogLevel.Error, Message = "Periodic skill resync reported errors: {Code} {Message}")]
    private static partial void LogSkillSyncFailed(ILogger logger, string code, string message);

    [LoggerMessage(EventId = 1841, Level = LogLevel.Error, Message = "Periodic skill resync threw; this tick's skill sync did not complete, the charter and process syncs still ran.")]
    private static partial void LogSkillSyncThrew(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1842, Level = LogLevel.Error, Message = "Periodic charter resync reported errors: {Code} {Message}")]
    private static partial void LogCharterSyncFailed(ILogger logger, string code, string message);

    [LoggerMessage(EventId = 1843, Level = LogLevel.Error, Message = "Periodic charter resync threw; this tick's charter sync did not complete, the skill and process syncs still ran.")]
    private static partial void LogCharterSyncThrew(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1844, Level = LogLevel.Error, Message = "Periodic process definition resync reported errors: {Error}")]
    private static partial void LogProcessSyncFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 1845, Level = LogLevel.Error, Message = "Periodic process definition resync threw; this tick's process sync did not complete, the skill and charter syncs still ran.")]
    private static partial void LogProcessSyncThrew(ILogger logger, Exception exception);
}
