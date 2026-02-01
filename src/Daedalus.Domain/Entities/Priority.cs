namespace Daedalus.Domain.Entities;

/// <summary>
///     Represents the priority level of a task within a project.
/// </summary>
public enum Priority
{
    /// <summary>Low priority - can be deferred.</summary>
    Low = 0,

    /// <summary>Medium priority - should be completed soon.</summary>
    Medium = 1,

    /// <summary>High priority - must be completed.</summary>
    High = 2,

    /// <summary>Critical priority - must be completed first.</summary>
    Critical = 3
}
