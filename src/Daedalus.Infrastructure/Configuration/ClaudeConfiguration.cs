namespace Daedalus.Infrastructure.Configuration;

/// <summary>
///     The Anthropic settings <c>AgentFactory</c> reads, bound from <see cref="SectionName"/>.
/// </summary>
public sealed class ClaudeConfiguration
{
    public const string SectionName = "ExternalServices:Llm:Claude";

    /// <summary>The default model, used when <see cref="Model"/> is not configured.</summary>
    public const string DefaultModel = "claude-sonnet-4-20250514";

    /// <summary>
    ///     Anthropic API key. When it is not configured, the <c>ANTHROPIC_API_KEY</c> environment variable is used.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>The model to invoke.</summary>
    public string Model { get; set; } = DefaultModel;

    /// <summary>The maximum output tokens of one invocation.</summary>
    public int MaxTokens { get; set; } = 8192;
}
