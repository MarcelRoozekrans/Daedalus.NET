namespace Daedalus.Application.Abstractions;

/// <summary>
///     Options for configuring a subagent invocation.
///     Subagents are isolated LLM context windows that execute independently,
///     following the Ralph Wiggum technique of context window preservation.
/// </summary>
public sealed class SubagentOptions
{
    /// <summary>
    ///     Optional system prompt to set the subagent's behavior and constraints.
    /// </summary>
    public string? SystemPrompt { get; init; }

    /// <summary>
    ///     Maximum tokens for the subagent response (default: 16384).
    /// </summary>
    public int MaxTokens { get; init; } = 16384;

    /// <summary>
    ///     Timeout for the subagent invocation (default: 120 seconds).
    ///     Subagents may execute expensive operations, so higher timeout than primary loop.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>
    ///     Whether to enable extended thinking for complex reasoning tasks (default: false).
    ///     When enabled, the model exposes its chain-of-thought before responding.
    /// </summary>
    public bool EnableExtendedThinking { get; init; }

    /// <summary>
    ///     Budget tokens for extended thinking when enabled (default: 4096).
    /// </summary>
    public int ThinkingBudgetTokens { get; init; } = 4096;

    /// <summary>
    ///     Optional model override for this subagent. If null, uses the service default.
    ///     Allows using cheaper/faster models for simple subagent tasks.
    /// </summary>
    public string? ModelOverride { get; init; }

    /// <summary>
    ///     Optional label for tracking and logging this subagent's purpose.
    /// </summary>
    public string? Label { get; init; }
}
