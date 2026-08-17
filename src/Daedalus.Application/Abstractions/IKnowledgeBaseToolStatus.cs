namespace Daedalus.Application.Abstractions;

/// <summary>
///     Indicates whether the knowledge base MCP tools are available.
///     When available, LearningsEnrichmentMiddleware uses slim mode (summary only).
///     When unavailable, falls back to full text injection.
/// </summary>
public interface IKnowledgeBaseToolStatus
{
    /// <summary>Whether the search_learnings and search_failure_patterns tools are registered.</summary>
    bool AreToolsAvailable { get; }

    /// <summary>The count of available learnings in the knowledge base.</summary>
    int LearningsCount { get; }

    /// <summary>The count of known failure patterns.</summary>
    int FailurePatternsCount { get; }
}
