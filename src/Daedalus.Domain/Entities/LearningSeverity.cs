namespace Daedalus.Domain.Entities;

/// <summary>
///     Severity level for structured learning entries.
///     Higher severity learnings are prioritized in prompt enrichment.
/// </summary>
public enum LearningSeverity
{
    /// <summary>Low priority — nice to know but not critical.</summary>
    Low = 0,

    /// <summary>Medium priority — useful context for task execution.</summary>
    Medium = 1,

    /// <summary>High priority — critical knowledge that prevents failures.</summary>
    High = 2,

    /// <summary>Critical — must-know information (e.g., blocking error patterns).</summary>
    Critical = 3
}
