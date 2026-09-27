namespace Daedalus.Agents.Workflow;

/// <summary>
///     The timing rules that tie <see cref="WorkflowOutboxDispatchOptions"/> to the rest of a workflow dispatch:
///     the lease must outlast one dispatch, and the stranded-run threshold must outlast everything that can hold a
///     healthy run's <c>updated_at</c> still. Both are derived from the same inputs here, so the lease, the turn
///     deadline and the stranded-run sweep cannot drift apart silently.
/// </summary>
/// <remarks>
///     A dispatch is, at most, the dispatch-gate wait followed by one agent turn. The gate wait is a parameter
///     because the gate belongs to the run workspace work: until a gate exists the composition root passes
///     <see cref="TimeSpan.Zero"/>, and the task that adds the gate passes its readiness timeout, which moves
///     both the lease check and the threshold with it.
/// </remarks>
internal static class WorkflowDispatchTiming
{
    /// <summary>
    ///     Added on top of the terms <see cref="StrandedAfter"/> sums, so the threshold strictly exceeds them. It
    ///     covers the poll interval before a released or retried message is claimed again, the sweep's own
    ///     one-minute tick, and clock skew between replicas, each of which delays a healthy run's next write a
    ///     little further.
    /// </summary>
    internal static readonly TimeSpan StrandedMargin = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     Rejects a <see cref="WorkflowOutboxDispatchOptions"/> whose lease cannot hold a dispatch, or which the
    ///     outbox store cannot record. Thrown at registration, like every other configuration check in
    ///     <c>AddDaedalusAgents</c>, so a bad value stops the host at boot rather than double-dispatching a paid
    ///     agent turn later.
    /// </summary>
    /// <param name="options">The dispatch options to check.</param>
    /// <param name="turnDeadline">The agent turn's wall-clock deadline, <c>DetachedRuns:DeadlineSeconds</c>.</param>
    /// <param name="gateWait">The longest the dispatch gate can wait before the turn starts.</param>
    /// <exception cref="InvalidOperationException">One or more rules are broken; the message lists each one.</exception>
    public static void Validate(WorkflowOutboxDispatchOptions options, TimeSpan turnDeadline, TimeSpan gateWait)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.LeaseDuration <= turnDeadline + gateWait)
        {
            failures.Add(
                $"LeaseDuration ({options.LeaseDuration}) must exceed the longest dispatch: the dispatch-gate wait " +
                $"({gateWait}) plus the turn deadline ({turnDeadline}). A lease that runs out mid-turn lets another " +
                "replica claim the message and run the same agent turn again.");
        }

        if (options.LeaseDuration > WorkflowOutboxDispatchOptions.MaxLeaseDuration)
        {
            failures.Add(
                $"LeaseDuration ({options.LeaseDuration}) must be at most {WorkflowOutboxDispatchOptions.MaxLeaseDuration}.");
        }

        if (string.IsNullOrWhiteSpace(options.HostId))
        {
            failures.Add("HostId must not be empty or whitespace.");
        }
        else if (options.HostId.Length > WorkflowOutboxDispatchOptions.MaxHostIdLength)
        {
            failures.Add($"HostId must be at most {WorkflowOutboxDispatchOptions.MaxHostIdLength} characters.");
        }

        if (options.BatchSize != 1)
        {
            failures.Add(
                $"BatchSize ({options.BatchSize}) must be 1: leases are renewed per entry, right before its " +
                "dispatch, so a larger batch lets the later leases run out behind multi-minute turns.");
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                "Invalid workflow outbox dispatch options: " + string.Join(' ', failures));
        }
    }

    /// <summary>
    ///     How long a run may go without a write before <see cref="Thalos.Workflow.WorkflowRunReconciler.SweepAsync"/>
    ///     terminates it as stranded. It must exceed the longest a healthy run's <c>updated_at</c> can stand still:
    ///     a crashed host's lease running out (<see cref="WorkflowOutboxDispatchOptions.LeaseDuration"/>), then the
    ///     next dispatch's gate wait and turn, then the retry backoff before the attempt that finally advances it
    ///     (<see cref="WorkflowOutboxDispatchOptions.RetryBackoffWindow"/>), plus <see cref="StrandedMargin"/>.
    ///     With a 20-minute lease, a 10-minute gate wait and a 5-minute turn that is just over 44 minutes; with no
    ///     gate it is just over 34.
    /// </summary>
    /// <param name="options">The dispatch options whose lease and backoff are two of the terms.</param>
    /// <param name="turnDeadline">The agent turn's wall-clock deadline, <c>DetachedRuns:DeadlineSeconds</c>.</param>
    /// <param name="gateWait">The longest the dispatch gate can wait before the turn starts.</param>
    public static TimeSpan StrandedAfter(WorkflowOutboxDispatchOptions options, TimeSpan turnDeadline, TimeSpan gateWait)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.LeaseDuration + gateWait + turnDeadline + options.RetryBackoffWindow + StrandedMargin;
    }
}
