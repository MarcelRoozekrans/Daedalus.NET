using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Thalos.Workflow;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     Fires <see cref="WorkflowRunReconciler.SweepAsync"/> once a minute — a plain <see cref="PeriodicTimer"/>
///     <see cref="BackgroundService"/>, following <see cref="Daedalus.Agents.Scheduling.ScheduleSweeperService"/>'s
///     pattern. Thalos ships <see cref="WorkflowRunReconciler"/> with no timer of its own, deliberately: hosting
///     it is the consumer's job, the same way <see cref="WorkflowNodeDispatcher"/> leaves dispatch invocation to
///     its host.
/// </summary>
/// <remarks>
///     <b>The stranded threshold is 30 minutes — sized against a formula, not a round number.</b> Per
///     <see cref="WorkflowRunReconciler.SweepAsync"/>'s own XML doc, the threshold must comfortably exceed both
///     the longest a healthy node's agent turn runs and the outbox's own retry-and-backoff window, during which
///     a run's <c>updated_at</c> does not advance. There is a third term easy to miss the first time: this
///     sweep's own <c>maxVisits</c>-style limit is on <em>turn length</em>, not on how many turns
///     <see cref="WorkflowOutboxDispatchService"/> dispatches before one gets a turn — a run queued behind others
///     in the same poll batch has its <c>updated_at</c> frozen for the whole queue ahead of it, not just its own
///     turn. The threshold must therefore exceed
///     <c>WorkflowOutboxDispatchOptions.BatchSize * longest turn + retry-and-backoff window</c>, not just the
///     last two terms alone — sizing it only against a single turn and the backoff window, as an earlier version
///     of this comment did, silently assumed a batch size of 1 without saying so, and would have started failing
///     healthy runs the day someone raised <c>BatchSize</c> back up without revisiting this number.
///     <para>
///     Today's numbers: <see cref="WorkflowOutboxDispatchOptions.BatchSize"/> is 1 (see that property's own
///     remarks for why it is not batched at all), so the queueing term drops out. A turn's ceiling is
///     configuration, not a guess — <see cref="BudgetedSubagentRunner"/> stamps <c>DetachedRunOptions.DeadlineSeconds</c>
///     (300s / 5 minutes in <c>appsettings.json</c>) onto every workflow-run turn. With
///     <see cref="WorkflowOutboxDispatchOptions"/>'s retry defaults (<c>MaxAttempts</c> 8, <c>RetryBaseDelay</c>
///     2s) the backoff alone reaches roughly four minutes before dead-lettering. <c>1 * 5 minutes + ~4 minutes</c>
///     is comfortably under 30 — the margin this threshold keeps is deliberate, not just generous, because
///     <c>BatchSize</c>, <c>DeadlineSeconds</c> and <c>MaxAttempts</c>/<c>RetryBaseDelay</c> can all change
///     independently of this file. Sizing this only against agent-turn length would terminate runs whose next
///     delivery attempt was about to succeed.
///     </para>
/// </remarks>
internal sealed partial class WorkflowStrandedRunSweepService(
    WorkflowRunReconciler reconciler,
    ILogger<WorkflowStrandedRunSweepService> logger) : BackgroundService
{
    private static readonly TimeSpan StrandedAfter = TimeSpan.FromMinutes(30);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                var terminated = await reconciler.SweepAsync(StrandedAfter, stoppingToken).ConfigureAwait(false);
                if (terminated > 0)
                {
                    LogTerminated(logger, terminated);
                }
            }
            // A normal host shutdown cancels stoppingToken mid-sweep; that is not a sweep failure and must
            // propagate so the BackgroundService loop above stops promptly instead of logging a spurious error.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogSweepFailed(logger, ex);
            }
        }
    }

    [LoggerMessage(EventId = 1820, Level = LogLevel.Information, Message = "Workflow sweep terminated {Count} stranded run(s).")]
    private static partial void LogTerminated(ILogger logger, int count);

    [LoggerMessage(EventId = 1821, Level = LogLevel.Error, Message = "Workflow stranded-run sweep failed; the next tick will retry.")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);
}
