namespace Daedalus.Application.DTOs.Scheduling;

/// <summary>
///     The verdict on where a scheduled run digest went: delivered, still running, not yet due, overdue, stranded, etc.
///     The eight real verdicts from the UI contract, plus <see cref="Unknown"/> as a zero sentinel.
/// </summary>
/// <remarks>
///     <see cref="Unknown"/> is member 0 deliberately, so a <c>default(RunVerdict)</c> is never mistaken
///     for a real answer. <c>AgentErrorCode</c> numbers a real value as 0 and that produced false-passing
///     tests three times on one branch in this codebase.
/// </remarks>
public enum RunVerdict
{
    /// <summary>Sentinel value for an uninitialized or missing verdict. Never returned by the service.</summary>
    Unknown = 0,

    /// <summary>The digest was successfully delivered to its target.</summary>
    Delivered = 1,

    /// <summary>The run is currently in progress; a step is still executing or queued.</summary>
    Running = 2,

    /// <summary>The scheduled occurrence has not yet become due.</summary>
    NotYetDue = 3,

    /// <summary>The scheduled occurrence is overdue; it has not run though it should have.</summary>
    Overdue = 4,

    /// <summary>The run reached a terminal state without completion; left in the queue without a handler.</summary>
    Stranded = 5,

    /// <summary>The run completed but whether delivery succeeded is unknown; state may be inconsistent.</summary>
    DeliveryUnknown = 6,

    /// <summary>The run failed; <see cref="RunDiagnosis.LastError"/> says why.</summary>
    Failed = 7,

    /// <summary>The run was not delivered; it did not run at all despite being due.</summary>
    Undelivered = 8,

    /// <summary>
    ///     The schedule is switched off, so this occurrence will never fire. Outranks every other verdict:
    ///     a disabled schedule's <see cref="Daedalus.Domain.Entities.ScheduledRun.NextRunAt"/> goes stale in the
    ///     past because the sweeper skips it, which would otherwise read as <see cref="Overdue"/> — and "the
    ///     sweeper is broken" is a very different call-out from "somebody turned this off".
    /// </summary>
    /// <remarks>
    ///     Appended as member 9 rather than slotted in near the other "never ran" verdicts, because these values
    ///     are serialized across the API boundary and renumbering would silently change the meaning of every
    ///     value already on the wire.
    /// </remarks>
    Disabled = 9,
}

/// <summary>
///     The diagnostic result for a single scheduled run: its verdict, the schedule's identity,
///     the occurrence and its state, and the execution's attempt history.
/// </summary>
/// <param name="Verdict">Where this run died, or that it did not.</param>
/// <param name="ScheduleId">The schedule this run belongs to.</param>
/// <param name="ScheduleName">The schedule's human-assigned name.</param>
/// <param name="OccurrenceAtUtc">The occurrence being diagnosed. Once an execution exists this is a PAST instant; see <paramref name="NextRunAtUtc"/> for the upcoming one.</param>
/// <param name="StepReached">The step the run reached, as <see cref="Daedalus.Domain.Entities.RunStep"/>.</param>
/// <param name="FailedAtStep">The step a failure happened at, if one did.</param>
/// <param name="LastError">Why the run failed, if it did.</param>
/// <param name="Attempts">How many attempts the run has accumulated.</param>
/// <param name="DeadLetterError">Why delivery was abandoned, if it was.</param>
/// <param name="RetryCount">How many delivery attempts were made before abandonment.</param>
/// <param name="CreatedAtUtc">When the execution row was created.</param>
/// <param name="UpdatedAtUtc">When the execution row last advanced.</param>
/// <param name="ExecutionId">The execution, or <see cref="Guid.Empty"/> when no run was ever claimed.</param>
/// <param name="Enabled">Whether the schedule is switched on. See the remarks.</param>
/// <param name="NextRunAtUtc">The schedule's upcoming occurrence. See the remarks.</param>
/// <remarks>
///     <paramref name="Enabled"/> and <paramref name="NextRunAtUtc"/> describe the SCHEDULE, not the run, and are
///     appended for the overview grid, which shows both alongside the last run. They are not derivable from the
///     other members: once an execution exists <paramref name="OccurrenceAtUtc"/> is the occurrence that already
///     happened, so without <paramref name="NextRunAtUtc"/> the next one cannot be rendered at all.
///     <paramref name="NextRunAtUtc"/> is populated on every diagnosis, including a disabled schedule's, where it
///     is the stale value the sweeper will never act on — the page has <paramref name="Enabled"/> to know that.
/// </remarks>
public record RunDiagnosis(
    RunVerdict Verdict,
    Guid ScheduleId,
    string ScheduleName,
    DateTime OccurrenceAtUtc,
    int StepReached,
    int? FailedAtStep,
    string? LastError,
    int Attempts,
    string? DeadLetterError,
    int? RetryCount,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    Guid ExecutionId,
    bool Enabled,
    DateTime? NextRunAtUtc);
