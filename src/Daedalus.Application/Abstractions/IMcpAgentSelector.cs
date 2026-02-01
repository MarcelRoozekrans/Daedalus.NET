using CSharpFunctionalExtensions;

namespace Daedalus.Application.Abstractions;

/// <summary>
///     Fetches appropriate agents and skills from the Awesome Copilot MCP server
///     for a given prompt or task.
/// </summary>
public interface IMcpAgentSelector
{
    /// <summary>
    ///     Searches for the best-matching agents/skills from Awesome Copilot
    ///     based on the prompt content and context.
    /// </summary>
    /// <param name="prompt">The prompt text to analyze.</param>
    /// <param name="tags">Optional: specific tags to filter by (e.g., "devops", "security").</param>
    /// <param name="maxResults">Maximum number of results to return (default: 5).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    ///     A list of recommended agents/skills ranked by relevance.
    /// </returns>
    Task<Result<IReadOnlyList<AgentMetadata>>> FindAgentsForPromptAsync(
        string prompt,
        IReadOnlyList<string>? tags = null,
        int maxResults = 5,
        CancellationToken ct = default);

    /// <summary>
    ///     Gets all available agents grouped by organization and category.
    /// </summary>
    Task<Result<IDictionary<string, IReadOnlyList<AgentMetadata>>>> GetAvailableAgentsAsync(
        CancellationToken ct = default);

    /// <summary>
    ///     Fetches a specific agent/skill by ID.
    /// </summary>
    Task<Result<AgentMetadata>> GetAgentAsync(
        string agentId,
        CancellationToken ct = default);
}
