namespace Daedalus.Domain.Entities;

/// <summary>
///     The status of a board task. Values 0 to 4 are stored. A task with a manufacture run shows the run's status
///     instead, mapped on read; <see cref="AwaitingApproval"/> and <see cref="Cancelled"/> exist only there and are
///     <b>never stored</b>: the <c>CK_Tasks_Status_Stored</c> check constraint refuses them.
/// </summary>
public enum TaskStatus
{
    /// <summary>Not started. A new task, or a task whose run is unknown on this host.</summary>
    Pending = 0,

    /// <summary>Its manufacture run is running, or, for a task from before phase 2.8, the retired loop had claimed it.</summary>
    InProgress = 1,

    /// <summary>Its manufacture run succeeded, or the retired loop completed it.</summary>
    Completed = 2,

    /// <summary>Its manufacture run failed, or the retired loop ran out of iterations.</summary>
    Failed = 3,

    /// <summary>Stored only by the retired loop, for a worker session that crashed. No new task reaches it.</summary>
    Abandoned = 4,

    /// <summary>Derived, never stored: the run is parked at its human approval gate.</summary>
    AwaitingApproval = 5,

    /// <summary>Derived, never stored: the run was cancelled.</summary>
    Cancelled = 6,
}
