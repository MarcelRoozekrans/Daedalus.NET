using CSharpFunctionalExtensions;

namespace Daedalus.Application.Abstractions;

/// <summary>
///     Selects and manages agents for prompt enhancement.
///     Extended to support execution capabilities.
/// </summary>
public interface IAgentSelector
{
    /// <summary>
    ///     Finds agents matching a prompt and optionally executes the best one.
    /// </summary>
    /// <param name="prompt">The prompt to analyze.</param>
    /// <param name="executeTopAgent">If true, also execute the best matching agent.</param>
    /// <param name="tags">Optional tag filters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Ranked agents, and optionally the execution context from the top agent.</returns>
    Task<Result<(IReadOnlyList<AgentMetadata> Agents, AgentExecutionContext? TopAgentExecution)>>
        FindAndExecuteAgentsAsync(
            string prompt,
            bool executeTopAgent = false,
            IReadOnlyList<string>? tags = null,
            CancellationToken ct = default);

    /// <summary>
    ///     Gets all available agents with their capability descriptions.
    /// </summary>
    Task<Result<IDictionary<string, IReadOnlyList<AgentMetadata>>>> GetAvailableAgentsAsync(
        CancellationToken ct = default);

    /// <summary>
    ///     Gets a specific agent by ID.
    /// </summary>
    Task<Result<AgentMetadata>> GetAgentAsync(
        string agentId,
        CancellationToken ct = default);
}
