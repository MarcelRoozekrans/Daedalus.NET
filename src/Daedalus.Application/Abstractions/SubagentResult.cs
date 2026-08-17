namespace Daedalus.Application.Abstractions;

/// <summary>
///     Result from a subagent invocation.
/// </summary>
public sealed class SubagentResult
{
    /// <summary>
    ///     The text response from the subagent.
    /// </summary>
    public required string Response { get; init; }

    /// <summary>
    ///     The label identifying this subagent's purpose (if provided).
    /// </summary>
    public string? Label { get; init; }

    /// <summary>
    ///     Duration of the subagent invocation.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    ///     Input tokens consumed by this subagent invocation.
    /// </summary>
    public int InputTokens { get; init; }

    /// <summary>
    ///     Output tokens produced by this subagent invocation.
    /// </summary>
    public int OutputTokens { get; init; }

    /// <summary>
    ///     Whether extended thinking was used for this invocation.
    /// </summary>
    public bool UsedExtendedThinking { get; init; }

    /// <summary>
    ///     The thinking content if extended thinking was enabled (null otherwise).
    /// </summary>
    public string? ThinkingContent { get; init; }
}
