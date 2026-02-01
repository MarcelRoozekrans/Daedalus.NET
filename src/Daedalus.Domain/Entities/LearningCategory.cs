namespace Daedalus.Domain.Entities;

/// <summary>
///     Categories for structured learning entries.
///     Enables filtered retrieval of relevant learnings during prompt enrichment.
/// </summary>
public enum LearningCategory
{
    /// <summary>Error patterns and their resolutions (e.g., "CS1061 → missing using directive").</summary>
    ErrorPattern = 0,

    /// <summary>Successful approaches that led to completion (e.g., "async streaming worked for large datasets").</summary>
    SuccessPattern = 1,

    /// <summary>Code conventions discovered during execution (e.g., "project uses primary constructors").</summary>
    CodeConvention = 2,

    /// <summary>Dependency and library information (e.g., "uses ZLinq 1.5.4 for zero-allocation LINQ").</summary>
    DependencyInfo = 3,

    /// <summary>Architecture decisions and patterns (e.g., "Railway-Oriented Programming with Result types").</summary>
    ArchitectureDecision = 4
}
