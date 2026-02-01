using CSharpFunctionalExtensions;

namespace Daedalus.Application.Abstractions;

/// <summary>
///     Executes agents (locally or remotely) and manages their contexts.
///     Supports both single agent execution and multi-agent chains.
/// </summary>
public interface IAgentExecutor
{
    /// <summary>
    ///     Executes a single agent with the given prompt.
    /// </summary>
    Task<Result<AgentExecutionContext>> ExecuteAgentAsync(
        AgentMetadata agent,
        string prompt,
        CancellationToken ct = default);

    /// <summary>
    ///     Executes multiple agents in sequence, passing output of one as input to the next.
    /// </summary>
    /// <param name="agents">Agents to execute in order.</param>
    /// <param name="initialPrompt">The starting prompt.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The execution chain with all agent contexts.</returns>
    Task<Result<AgentExecutionChain>> ExecuteAgentChainAsync(
        IReadOnlyList<AgentMetadata> agents,
        string initialPrompt,
        CancellationToken ct = default);

    /// <summary>
    ///     Retrieves execution history for a specific agent.
    /// </summary>
    Task<Result<IReadOnlyList<AgentExecutionContext>>> GetAgentHistoryAsync(
        string agentId,
        CancellationToken ct = default);
}
