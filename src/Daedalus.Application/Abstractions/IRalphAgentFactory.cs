using CSharpFunctionalExtensions;

namespace Daedalus.Application.Abstractions;

/// <summary>
///     Factory for creating and invoking LLM agents backed by the Microsoft Agent Framework.
///     Replaces <c>ILlmService</c> + <c>ILlmServiceFactory</c> with a unified interface
///     that handles MCP tool attachment, provider configuration, and subagent lifecycle.
/// </summary>
/// <remarks>
///     The primary provider is Anthropic Claude. MCP tools (Context7, Awesome Copilot)
///     are automatically attached to agents created by this factory.
///     The Application layer is shielded from framework-specific types (<c>ChatClientAgent</c>,
///     <c>IChatClient</c>, etc.) — only <c>Result&lt;string&gt;</c> and <c>SubagentResult</c>
///     cross the boundary.
/// </remarks>
public interface IRalphAgentFactory
{
    /// <summary>
    ///     Invokes the primary LLM agent with MCP tools pre-attached.
    ///     This is the single entry point for all LLM invocations in the Ralph loop —
    ///     no MCP branching is needed at the call site.
    /// </summary>
    /// <param name="prompt">The user prompt to send to the LLM.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The LLM response text or failure.</returns>
    Task<Result<string>> InvokeAsync(
        string prompt,
        CancellationToken ct = default);

    /// <summary>
    ///     Invokes a subagent with its own isolated context window.
    ///     Subagents execute independently, preserving the primary context window budget.
    ///     This is the key Ralph Wiggum technique: the primary context operates as a scheduler,
    ///     delegating expensive work to subagents.
    /// </summary>
    /// <param name="prompt">The prompt for the subagent to execute.</param>
    /// <param name="options">Subagent configuration (system prompt, token budget, timeout).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The subagent result including response text and token usage.</returns>
    Task<Result<SubagentResult>> InvokeSubagentAsync(
        string prompt,
        SubagentOptions options,
        CancellationToken ct = default);

    /// <summary>
    ///     Runs multiple subagents in parallel, each with its own isolated context window.
    ///     Supports the Ralph Wiggum pattern of spawning parallel subagents for
    ///     research, code analysis, and implementation tasks.
    /// </summary>
    /// <param name="prompts">The prompts for each subagent to execute.</param>
    /// <param name="options">Shared subagent configuration options.</param>
    /// <param name="maxParallelism">Maximum number of concurrent subagents (default: 10).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Results from all successful subagents.</returns>
    Task<Result<IReadOnlyList<SubagentResult>>> RunParallelSubagentsAsync(
        IReadOnlyList<string> prompts,
        SubagentOptions options,
        int maxParallelism = 10,
        CancellationToken ct = default);
}
