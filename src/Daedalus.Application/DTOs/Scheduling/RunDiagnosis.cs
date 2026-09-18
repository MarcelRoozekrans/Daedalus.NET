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
}

/// <summary>
///     The diagnostic result for a single scheduled run: its verdict, the schedule's identity,
///     the occurrence and its state, and the execution's attempt history.
/// </summary>
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
    Guid ExecutionId);
