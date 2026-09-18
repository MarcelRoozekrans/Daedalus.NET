using ZeroAlloc.Outbox;

namespace Daedalus.Agents.Scheduling;

/// <summary>
///     A scheduled run became due. Written by <see cref="ScheduleSweeperService"/> inside the same transaction that
///     advances the row's <c>NextRunAt</c>, so the advance and the trigger commit together or not at all — a crash
///     between them can neither lose the run nor fire it twice.
/// </summary>
/// <remarks>
///     Outbox delivery is at-least-once, so this message can arrive more than once. That is safe by construction:
///     a <c>UNIQUE (ScheduleId, OccurrenceAt)</c> constraint on the <c>ScheduledRunExecutions</c> table (added in a
///     later task) makes <paramref name="ScheduleId"/> plus <paramref name="OccurrenceAtUtc"/> the idempotency key
///     — a redelivery's insert finds the row already present and, via <c>ON CONFLICT DO NOTHING</c>, inserts
///     nothing and stops there rather than starting the run a second time. It does not fail a constraint: see
///     <see cref="ScheduledRunExecutionStore"/>'s remarks for why an exception-based catch-23505 approach was
///     rejected in favor of that single statement. The write itself is <see cref="ScheduledRunStore.ClaimAndEnqueueDueAsync"/>,
///     not <see cref="ScheduleSweeperService"/>. Idempotency comes from the database constraint, not from dedupe
///     logic here.
/// </remarks>
[OutboxMessage]
public sealed record ScheduledRunDue(Guid ScheduleId, DateTime OccurrenceAtUtc);
