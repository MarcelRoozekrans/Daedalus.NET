namespace Daedalus.Infrastructure.Configuration;

/// <summary>
///     Claude-specific configuration for the Anthropic API.
/// </summary>
public sealed class ClaudeConfiguration
{
    /// <summary>
    ///     Anthropic API key. Can also be set via ANTHROPIC_API_KEY environment variable.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    ///     Model to use (default: "claude-sonnet-4-20250514").
    /// </summary>
    public string Model { get; set; } = "claude-sonnet-4-20250514";

    /// <summary>
    ///     Maximum tokens for primary invocations (default: 8192).
    /// </summary>
    public int MaxTokens { get; set; } = 8192;

    /// <summary>
    ///     Request timeout in milliseconds (default: 60000).
    /// </summary>
    public int Timeout { get; set; } = 60000;

    /// <summary>
    ///     Whether to enable Claude as an LLM provider (default: false).
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    ///     Maximum parallel subagents (default: 10).
    /// </summary>
    public int MaxParallelSubagents { get; set; } = 10;

    /// <summary>
    ///     Default subagent model override. If null, uses the primary model.
    ///     Allows using cheaper models (e.g., haiku) for simple subagent tasks.
    /// </summary>
    public string? SubagentModel { get; set; }
}
