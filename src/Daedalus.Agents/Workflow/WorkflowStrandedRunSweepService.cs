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
///     <b>The stranded threshold is 30 minutes — sized against the outbox, not the sweep's own one-minute tick.</b>
///     Per <see cref="WorkflowRunReconciler.SweepAsync"/>'s own XML doc, the threshold must comfortably exceed
///     both the longest a healthy node's agent turn runs (minutes, routinely) and the outbox's own
///     retry-and-backoff window, during which a run's <c>updated_at</c> does not advance. With
///     <see cref="WorkflowOutboxDispatchOptions"/>'s defaults (<c>MaxAttempts</c> 8, <c>RetryBaseDelay</c> 2s) the
///     backoff alone reaches roughly four minutes before dead-lettering; <c>docs/workflow.md</c>'s own worked
///     example against those same numbers recommends 30 minutes, which is what this uses. Sizing this only
///     against agent-turn length would terminate runs whose next delivery attempt was about to succeed.
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
