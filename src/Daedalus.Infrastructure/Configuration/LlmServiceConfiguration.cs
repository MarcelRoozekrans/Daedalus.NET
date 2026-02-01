namespace Daedalus.Infrastructure.Configuration;

/// <summary>
///     Configuration for LLM service integration
/// </summary>
public sealed class LlmServiceConfiguration
{
    /// <summary>
    ///     Default provider (e.g., "copilot", "claude")
    /// </summary>
    public string Provider { get; set; } = "copilot";

    /// <summary>
    ///     API endpoint
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    ///     API key for authentication
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    ///     Request timeout in milliseconds
    /// </summary>
    public int Timeout { get; set; } = 30000;

    /// <summary>
    ///     Claude-specific configuration
    /// </summary>
    public ClaudeConfiguration Claude { get; set; } = new();

    /// <summary>
    ///     Additional provider-specific settings
    /// </summary>
#pragma warning disable CA2227 // Collection properties should be read-only - configuration pattern requires settable
#pragma warning disable MA0016 // Prefer using collection abstraction instead of implementation - configuration pattern requires Dictionary
    public Dictionary<string, object> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
#pragma warning restore MA0016
#pragma warning restore CA2227
}
