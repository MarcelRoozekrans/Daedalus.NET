using ZeroAlloc.Results;

namespace Daedalus.Application.Abstractions;

/// <summary>
///     Factory for creating and invoking LLM agents backed by the Microsoft Agent Framework.
///     Replaces <c>ILlmService</c> + <c>ILlmServiceFactory</c> with a unified interface
///     that handles MCP tool attachment and provider configuration.
/// </summary>
/// <remarks>
///     The primary provider is Anthropic Claude. MCP tools (Context7, Awesome Copilot)
///     are automatically attached to agents created by this factory.
///     The Application layer is shielded from framework-specific types (<c>ChatClientAgent</c>,
///     <c>IChatClient</c>, etc.) — only <c>Result&lt;LlmInvocationResult&gt;</c>
///     crosses the boundary.
/// </remarks>
public interface IAgentFactory
{
    /// <summary>
    ///     Invokes the LLM agent with MCP tools pre-attached.
    ///     This is the single entry point for all LLM invocations —
    ///     no MCP branching is needed at the call site.
    /// </summary>
    /// <param name="prompt">The user prompt to send to the LLM.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The LLM response text, token usage data, and model ID, or failure.</returns>
    Task<Result<LlmInvocationResult>> InvokeAsync(
        string prompt,
        CancellationToken ct = default);
}
