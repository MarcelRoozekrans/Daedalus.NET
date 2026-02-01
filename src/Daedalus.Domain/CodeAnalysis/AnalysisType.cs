namespace Daedalus.Domain.CodeAnalysis;

/// <summary>
///     Enumeration for code analysis types
/// </summary>
public enum AnalysisType
{
    None = 0,
    Refactor = 1,
    BugFix = 2,
    PerformanceOptimization = 3,
    SecurityAudit = 4,
    CodeReview = 5,
    TestGeneration = 6,
    DocumentationUpdate = 7,
    DependencyUpdate = 8
}
