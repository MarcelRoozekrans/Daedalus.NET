namespace Daedalus.Domain.Entities;

/// <summary>
///     Represents the estimated complexity level of a task.
/// </summary>
public enum Complexity
{
    /// <summary>Low complexity - straightforward implementation.</summary>
    Low = 0,

    /// <summary>Medium complexity - requires some design work.</summary>
    Medium = 1,

    /// <summary>High complexity - requires significant design and effort.</summary>
    High = 2
}
