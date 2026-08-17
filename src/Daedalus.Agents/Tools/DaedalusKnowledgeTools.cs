using System.ComponentModel;
using Daedalus.Infrastructure.Agents.Tools;
using Thalos;

namespace Daedalus.Agents.Tools;

/// <summary>
///     Daedalus knowledge exposed to Thalos agents: <c>daedalus__search_failure_patterns</c> only — learnings are recalled
///     automatically before each turn and through the Thalos <c>memory__*</c> tools. Registered under the tool-source name
///     <c>daedalus</c>.
/// </summary>
/// <remarks>
///     A thin wrapper over the existing Ralph MCP tool class (<see cref="DaedalusFailurePatternsTools"/>) so both agent
///     stacks share one implementation. Thalos's <c>LocalToolSource</c> creates a fresh DI scope per invocation, so the
///     scoped Infrastructure services behind these tools are never stale.
/// </remarks>
/// <param name="failures">The Ralph failure-patterns MCP tool class.</param>
[ThalosToolType]
public sealed class DaedalusKnowledgeTools(DaedalusFailurePatternsTools failures)
{
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
