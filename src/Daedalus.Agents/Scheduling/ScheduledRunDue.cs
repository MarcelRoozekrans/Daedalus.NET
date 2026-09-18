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
///     — a redelivery's insert finds the row already present, fails the constraint, and stops there rather than
///     starting the run a second time. Idempotency comes from that database constraint, not from dedupe logic here.
/// </remarks>
[OutboxMessage]
public sealed record ScheduledRunDue(Guid ScheduleId, DateTime OccurrenceAtUtc);
