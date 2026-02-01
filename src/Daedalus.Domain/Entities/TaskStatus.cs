namespace Daedalus.Domain.Entities;

/// <summary>
///     Represents the lifecycle status of a Ralph loop task.
/// </summary>
public enum TaskStatus
{
    /// <summary>Waiting to be picked up by a worker session.</summary>
    Pending = 0,

    /// <summary>Currently being executed by a worker session.</summary>
    InProgress = 1,

    /// <summary>Successfully completed with completion promise found.</summary>
    Completed = 2,

    /// <summary>Failed - max iterations reached without completion promise.</summary>
    Failed = 3,

    /// <summary>Abandoned - session crashed without completing work.</summary>
    Abandoned = 4
}
