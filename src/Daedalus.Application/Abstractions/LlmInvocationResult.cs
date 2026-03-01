namespace Daedalus.Application.Abstractions;

/// <summary>
///     Result of a primary LLM invocation, including response text and token usage.
/// </summary>
public sealed class LlmInvocationResult
{
    /// <summary>The LLM response text.</summary>
    public required string Response { get; init; }

    /// <summary>Number of input tokens consumed.</summary>
    public int InputTokens { get; init; }

    /// <summary>Number of output tokens produced.</summary>
    public int OutputTokens { get; init; }

    /// <summary>Model ID used for this invocation.</summary>
    public string? ModelId { get; init; }
}
