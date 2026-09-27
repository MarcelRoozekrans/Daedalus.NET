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
///     <b>The stranded threshold is derived, not a constant.</b> Per <see cref="WorkflowRunReconciler.SweepAsync"/>'s
///     own XML doc, the threshold must comfortably exceed everything during which a healthy run's
///     <c>updated_at</c> does not advance. <see cref="WorkflowDispatchTiming.StrandedAfter"/> sums those terms from
///     the same options the dispatch loop runs on: a crashed host's lease running out, the next dispatch's gate
///     wait and turn deadline, and the retry backoff, plus a margin. The composition root computes it once and
///     passes it in, so a change to the lease, the turn deadline or the retry budget moves this threshold with
///     it. The fixed 30 minutes this used to be was sized before leases existed; with a 20-minute lease it would
///     terminate runs a healthy replica was about to pick up.
///     <para>
///     A run queued behind others in one poll batch would add a queueing term, but
///     <see cref="WorkflowOutboxDispatchOptions.BatchSize"/> is held at 1 by
///     <see cref="WorkflowDispatchTiming.Validate"/>, so there is none.
///     </para>
/// </remarks>
internal sealed partial class WorkflowStrandedRunSweepService(
    WorkflowRunReconciler reconciler,
    TimeSpan strandedAfter,
    ILogger<WorkflowStrandedRunSweepService> logger) : BackgroundService
{
    /// <summary>How long a run may go without a write before the sweep terminates it as stranded.</summary>
    internal TimeSpan StrandedAfter { get; } = strandedAfter;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Runs one sweep. Internal, not private, so a test can drive a single sweep without waiting on the
    ///     one-minute timer. It never lets a failure escape, which would end every future sweep: only a
    ///     cancellation of <paramref name="stoppingToken"/> propagates.
    /// </summary>
    internal async Task SweepOnceAsync(CancellationToken stoppingToken)
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
        // Any other cancellation, such as a timeout inside the store, is a failed sweep: the next tick retries.
        catch (Exception ex) when (!IsStopping(ex, stoppingToken))
        {
            LogSweepFailed(logger, ex);
        }
    }

    /// <summary>True only for a cancellation caused by <paramref name="stoppingToken"/>, which means the host is stopping.</summary>
    private static bool IsStopping(Exception ex, CancellationToken stoppingToken) =>
        ex is OperationCanceledException && stoppingToken.IsCancellationRequested;

    [LoggerMessage(EventId = 1820, Level = LogLevel.Information, Message = "Workflow sweep terminated {Count} stranded run(s).")]
    private static partial void LogTerminated(ILogger logger, int count);

    [LoggerMessage(EventId = 1821, Level = LogLevel.Error, Message = "Workflow stranded-run sweep failed; the next tick will retry.")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);
}
