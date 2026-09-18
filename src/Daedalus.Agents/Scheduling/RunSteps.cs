using ZeroAlloc.Outbox;

namespace Daedalus.Agents.Scheduling;

/// <summary>
///     Run the scout subagent for one execution. Written inside the transaction that created the
///     <c>ScheduledRunExecutions</c> row, so the row and its first step commit together.
/// </summary>
/// <remarks>
///     <para>
///     Carries only the execution id: everything the step needs — delivery target, principal, roles — was copied
///     onto the row at claim time, and re-reading it there is what makes a redelivery see the current step rather
///     than a stale snapshot of one. Outbox delivery is at-least-once; the step check in
///     <c>ScheduledRunExecutionStore</c> makes a second delivery a no-op.
///     </para>
///     <para>
///     <b>The guarantee is bounded.</b> A crash after the subagent returns but before the step commits re-runs
///     that step and pays its tokens twice. This is inherent without a two-phase protocol with the model provider,
///     which does not exist. One step can be lost; the run cannot.
///     </para>
/// </remarks>
[OutboxMessage]
public sealed record RunScoutStep(Guid ExecutionId);

/// <summary>Run the writer subagent over the persisted findings. See <see cref="RunScoutStep"/> for the redelivery contract.</summary>
[OutboxMessage]
public sealed record RunWriterStep(Guid ExecutionId);

/// <summary>
///     Deliver the persisted digest. Its dispatcher writes <see cref="Channels.ChannelMessageQueued"/> in the same
///     transaction that sets <c>Step = Done</c> — the guarantee <c>ChannelMessageQueuedDispatcher</c> has been
///     waiting for since phase 1.4, which was never the saga's doing, only the transaction's.
/// </summary>
[OutboxMessage]
public sealed record DeliverDigest(Guid ExecutionId);
