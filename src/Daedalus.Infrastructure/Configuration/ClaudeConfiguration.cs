namespace Daedalus.Infrastructure.Configuration;

/// <summary>
///     The Anthropic settings <c>AgentFactory</c> reads, bound from <see cref="SectionName"/>.
/// </summary>
public sealed class ClaudeConfiguration
{
    public const string SectionName = "ExternalServices:Llm:Claude";

    /// <summary>The setting <see cref="Model"/> falls back to, so one setting decides the model of every Anthropic call.</summary>
    public const string FallbackModelKey = "Thalos:Anthropic:DefaultModel";

    /// <summary>
    ///     Anthropic API key. When it is not configured, the <c>ANTHROPIC_API_KEY</c> environment variable is used.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    ///     The model to invoke. When it is not configured it is <see cref="FallbackModelKey"/>; with neither, the
    ///     options fail validation.
    /// </summary>
    public string? Model { get; set; }

    /// <summary>The maximum output tokens of one invocation.</summary>
    public int MaxTokens { get; set; } = 8192;
}
