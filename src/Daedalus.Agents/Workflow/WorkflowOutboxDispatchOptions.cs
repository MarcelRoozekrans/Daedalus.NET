namespace Daedalus.Agents.Workflow;

/// <summary>
///     Configures <see cref="WorkflowOutboxDispatchService"/>'s poll loop. Deliberately its own type rather than
///     <c>ZeroAlloc.Outbox.OutboxOptions</c>: that type is a single, un-keyed options instance in this host's
///     container (<c>AddChannelOutbox</c> already binds one for the ApplicationDbContext-backed pipeline), so
///     configuring it a second time here would also rewrite the scheduling/channel outbox's polling interval,
///     batch size and retry budget out from under it.
/// </summary>
internal sealed class WorkflowOutboxDispatchOptions
{
    /// <summary>How often <see cref="WorkflowOutboxDispatchService"/> polls the workflow outbox table.</summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     The maximum number of pending rows fetched per poll. <b>Fixed at 1, not the 20
    ///     <c>docs/workflow.md</c>'s own example batches</b>: <see cref="WorkflowOutboxDispatchService.ProcessBatchAsync"/>
    ///     dispatches its batch serially, and each entry here is a full agent turn costing minutes, not a cheap
    ///     message worth amortising a round trip over. Batching 20 of these means the twentieth run in the batch
    ///     has its <c>updated_at</c> frozen for the time every run ahead of it takes to complete — nineteen
    ///     multi-minute turns comfortably exceeds a threshold sized only against one turn's length, so
    ///     <see cref="WorkflowStrandedRunSweepService"/> would fail healthy work still waiting its turn in the
    ///     queue. See that type's remarks for the threshold math this value is one term of.
    /// </summary>
    public int BatchSize { get; set; } = 1;

    /// <summary>
    ///     Attempts (including the first) before a message is dead-lettered. Matches <c>docs/workflow.md</c>'s own
    ///     worked example, whose backoff math the 30-minute stranded-run threshold in
    ///     <see cref="WorkflowStrandedRunSweepService"/> is sized against — changing this without revisiting that
    ///     threshold would reopen the gap <see cref="Thalos.Workflow.WorkflowRunReconciler.SweepAsync"/>'s own XML
    ///     doc warns about.
    /// </summary>
    public int MaxAttempts { get; set; } = 8;

    /// <summary>The base of the exponential backoff between retries: attempt <c>n</c> waits <c>RetryBaseDelay * 2^(n-1)</c>.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(2);
}
