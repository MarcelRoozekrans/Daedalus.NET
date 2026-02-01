namespace Daedalus.Application.Services;

/// <summary>
///     MCP integration configuration for Ralph loop.
/// </summary>
public sealed class McpIntegrationOptions
{
    /// <summary>
    ///     Enable/disable MCP integration (default: true)
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Dictionary of MCP server configurations keyed by server name
    /// </summary>
    public IReadOnlyDictionary<string, McpServerConfig> Servers { get; set; } =
        new Dictionary<string, McpServerConfig>(StringComparer.Ordinal);
}
