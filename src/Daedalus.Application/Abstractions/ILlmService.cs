using CSharpFunctionalExtensions;
using Daedalus.Application.Services;

namespace Daedalus.Application.Abstractions;

/// <summary>
///     Abstraction for LLM operations.
/// </summary>
public interface ILlmService
{
    /// <summary>
    ///     Gets the name of the LLM provider (e.g., "copilot", "claude").
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    ///     Whether this LLM service supports spawning subagents for parallel work.
    ///     Subagents allow offloading expensive operations (e.g., code analysis, test execution)
    ///     to separate context windows, preserving the primary context for scheduling.
    /// </summary>
    bool SupportsSubagents { get; }

    /// <summary>
    ///     Invokes the LLM with the given prompt.
    /// </summary>
    /// <param name="prompt">The prompt to send.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The LLM response or failure.</returns>
    Task<Result<string>> InvokeAsync(string prompt, CancellationToken ct);

    /// <summary>
    ///     Invokes the LLM with MCP server support.
    ///     Allows the LLM to access external tools via the Model Context Protocol.
    /// </summary>
    /// <param name="prompt">The prompt to send.</param>
    /// <param name="mcpOptions">MCP integration configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The LLM response or failure.</returns>
    Task<Result<string>> InvokeWithMcpAsync(string prompt, McpIntegrationOptions mcpOptions, CancellationToken ct);

    /// <summary>
    ///     Invokes a subagent with its own isolated context window.
    ///     The subagent executes the given prompt independently and returns its result.
    ///     This is the key Ralph Wiggum technique: the primary context operates as a scheduler,
    ///     delegating expensive work to subagents to preserve context window budget.
    /// </summary>
    /// <param name="prompt">The prompt for the subagent to execute.</param>
    /// <param name="options">Subagent configuration options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The subagent response or failure.</returns>
    Task<Result<SubagentResult>> InvokeSubagentAsync(
        string prompt,
        SubagentOptions options,
        CancellationToken ct);

    /// <summary>
    ///     Invokes multiple subagents in parallel, each with its own isolated context window.
    ///     Supports the Ralph Wiggum pattern of spawning many parallel subagents for
    ///     research, code analysis, and implementation tasks.
    /// </summary>
    /// <param name="prompts">The prompts for each subagent to execute.</param>
    /// <param name="options">Shared subagent configuration options.</param>
    /// <param name="maxParallelism">Maximum number of concurrent subagents (default: 10).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Results from all subagents, maintaining order with input prompts.</returns>
    Task<Result<IReadOnlyList<SubagentResult>>> InvokeParallelSubagentsAsync(
        IReadOnlyList<string> prompts,
        SubagentOptions options,
        int maxParallelism = 10,
        CancellationToken ct = default);
}
