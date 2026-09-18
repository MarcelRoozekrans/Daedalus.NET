namespace Daedalus.Domain.Entities;

/// <summary>
///     Which step of a scheduled run should happen next. Advances strictly forward:
///     <see cref="Pending"/> to <see cref="Scout"/> to <see cref="Writer"/> to <see cref="Deliver"/> to
///     <see cref="Done"/>, or to <see cref="Failed"/> from any of them.
/// </summary>
/// <remarks>
///     <see cref="Pending"/> is member 0 on purpose. A <c>default(RunStep)</c> then means "no step has been
///     enqueued yet" — a state no dispatcher acts on — rather than aliasing a real step. <c>AgentErrorCode</c>
///     numbers <c>Validation</c> as 0 and that produced false-passing tests three times on one branch, in three
///     files, from two implementers. The value is persisted as a string, so this numbering costs nothing to keep.
/// </remarks>
public enum RunStep
{
    /// <summary>The row exists but nothing has been enqueued. Never observable outside its creating transaction.</summary>
    Pending = 0,

    /// <summary>A <c>RunScoutStep</c> is queued or running.</summary>
    Scout = 1,

    /// <summary>The scout's findings are persisted; a <c>RunWriterStep</c> is queued or running.</summary>
    Writer = 2,

    /// <summary>The digest is persisted; a <c>DeliverDigest</c> is queued or running.</summary>
    Deliver = 3,

    /// <summary>Delivered. Terminal.</summary>
    Done = 4,

    /// <summary>A step failed; <see cref="ScheduledRunExecution.LastError"/> says which and why. Terminal.</summary>
    Failed = 5,
}
