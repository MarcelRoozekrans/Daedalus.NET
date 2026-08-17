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

    /// <summary>The count of known failure patterns. Learnings are not counted — they are recalled from the agent memory.</summary>
    int FailurePatternsCount { get; }
}
