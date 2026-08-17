using System.ComponentModel;
using Daedalus.Infrastructure.Agents.Tools;
using Thalos;

namespace Daedalus.Agents.Tools;

/// <summary>
///     Daedalus knowledge exposed to Thalos agents. Registered under the tool-source name <c>daedalus</c>, so the tools
///     appear to the model as <c>daedalus__search_learnings</c> and <c>daedalus__search_failure_patterns</c>.
/// </summary>
/// <remarks>
///     Thin wrappers over the existing Ralph MCP tool classes (<see cref="DaedalusLearningsTools"/> and
///     <see cref="DaedalusFailurePatternsTools"/>) so both agent stacks share one implementation. Thalos's
///     <c>LocalToolSource</c> creates a fresh DI scope per invocation, so the scoped Infrastructure services behind
///     these tools are never stale.
/// </remarks>
[ThalosToolType]
public sealed class DaedalusKnowledgeTools(DaedalusLearningsTools learnings, DaedalusFailurePatternsTools failures)
{
    /// <summary>Semantic (with keyword fallback) search over structured learnings from previous Daedalus tasks.</summary>
    [ThalosTool("search_learnings")]
    [Description(
        "Search past learnings from previous task executions using semantic similarity. " +
        "Use this when you encounter errors, need context about the codebase, or want to " +
        "learn from previous approaches that worked or failed. Returns matches with category, severity and resolution.")]
    public Task<string> SearchLearnings(
        [Description("Natural language description of what you're looking for")] string query,
        [Description("Filter to learnings from a specific project (optional project GUID)")] string? projectId = null,
        [Description("Maximum number of results (default: 5)")] int maxResults = 5,
        CancellationToken ct = default)
        => learnings.SearchLearnings(query, projectId, maxResults, ct);

    /// <summary>Finds known failure patterns (error → fix pairs) similar to the given error text.</summary>
    [ThalosTool("search_failure_patterns")]
    [Description(
        "Search known failure patterns and their solutions. Use this when you encounter " +
        "a build error, test failure, or runtime exception to find previously discovered fixes.")]
    public Task<string> SearchFailurePatterns(
        [Description("The error message or pattern to search for")] string errorMessage,
        [Description("Maximum number of results (default: 3)")] int maxResults = 3,
        CancellationToken ct = default)
        => failures.SearchFailurePatterns(errorMessage, maxResults, ct);
}
