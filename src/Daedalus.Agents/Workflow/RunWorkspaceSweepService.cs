using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Thalos.Workflow;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     Hosts <see cref="RunWorkspaceSweeper.SweepAsync"/> on a one-minute <see cref="PeriodicTimer"/>, sweeping once
///     immediately at start and then on every tick after. Thalos ships <see cref="RunWorkspaceSweeper"/> with no
///     timer of its own, deliberately — hosting it is the consumer's job; see <c>docs/workflow.md</c>, "Run
///     workspaces", §11, "Removing them".
/// </summary>
/// <remarks>
///     <para>
///     <b>This sweep is the only thing that removes a published run's worktree</b> (rulings R14 and R19).
///     <c>open-pull-request</c> (task B13) never removes the workspace itself, so a crash between its PR and the
///     run's advance never leaves a redelivery with no workspace; instead, once a run is
///     <see cref="WorkflowStatus.Succeeded"/> this sweep removes its worktree at the next pass, at most one minute
///     after the PR was opened.
///     </para>
///     <para>
///     <b>Swept at start, not only on the first tick.</b> <see cref="PeriodicTimer.WaitForNextTickAsync(CancellationToken)"/>
///     only completes after the first interval elapses, so a loop built only on it would leave every already-terminal
///     run's workspace on disk for up to a minute after the host starts — and, on a host that restarts inside that
///     minute, indefinitely. <see cref="ExecuteAsync"/> below runs the sweep first and waits after, a
///     <c>do</c>/<c>while</c> shape rather than <see cref="Daedalus.Agents.Scheduling.ScheduleSweeperService"/>'s
///     plain <c>while</c>, mirroring <see cref="WorkflowOutboxDispatchService"/> for the same reason.
///     </para>
///     <para>
///     <b>A tick must never let an exception escape</b> — the same rule
///     <see cref="Daedalus.Agents.Scheduling.ScheduleSweeperService"/> and <see cref="WorkflowOutboxDispatchService"/>
///     document for their own loops. <see cref="RunWorkspaceSweeper.SweepAsync"/> itself never throws for a single
///     failed removal — that is logged and skipped by the sweeper, and retried on the next pass — but it does throw
///     when listing the workspaces fails. <see cref="SweepOnceAsync"/> catches that, logs it, and lets the loop reach
///     its next tick; only a cancellation of the stopping token ends the loop.
///     </para>
/// </remarks>
internal sealed partial class RunWorkspaceSweepService(
    RunWorkspaceSweeper sweeper,
    TimeProvider timeProvider,
    ILogger<RunWorkspaceSweepService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval, timeProvider);

        do
        {
            await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>
    ///     Runs one sweep. Internal, not private, so a test can drive a single sweep without waiting on the timer —
    ///     the same seam <see cref="WorkflowStrandedRunSweepService.SweepOnceAsync"/> and
    ///     <see cref="WorkflowOutboxDispatchService.PollOnceAsync"/> give. Never lets a failure escape, which would
    ///     end every future sweep: only a cancellation of <paramref name="stoppingToken"/> propagates.
    /// </summary>
    internal async Task SweepOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            var removed = await sweeper.SweepAsync(stoppingToken).ConfigureAwait(false);
            if (removed > 0)
            {
                LogSwept(logger, removed);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            // A cancellation that did not come from the stopping token, such as an observer's own timeout while
            // listing the workspaces, is a failed sweep, not a request to stop the host.
            LogSweepFailed(logger, ex);
        }
    }

    [LoggerMessage(EventId = 1822, Level = LogLevel.Information, Message = "Run workspace sweep removed {Count} workspace(s).")]
    private static partial void LogSwept(ILogger logger, int count);

    [LoggerMessage(EventId = 1823, Level = LogLevel.Error, Message = "Run workspace sweep failed to list workspaces; the next sweep retries.")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);
}
