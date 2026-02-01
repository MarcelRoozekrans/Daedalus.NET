using System.Diagnostics.CodeAnalysis;

namespace Daedalus.Application.Services;

/// <summary>
///     Configuration for a single MCP server instance.
/// </summary>
[SuppressMessage("Design", "CA1056:URI-like properties should be of type System.Uri")]
public sealed class McpServerConfig
{
    /// <summary>
    ///     Type of MCP server: "http", "sse", "local", or "stdio"
    /// </summary>
    public string Type { get; set; } = "http";

    /// <summary>
    ///     URL endpoint for HTTP or SSE servers
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    ///     Command to run for stdio servers
    /// </summary>
    public string? Command { get; set; }

    /// <summary>
    ///     Command-line arguments for stdio servers
    /// </summary>
    public IReadOnlyList<string>? Args { get; set; }

    /// <summary>
    ///     Timeout for server operations in milliseconds (default: 10000)
    /// </summary>
    public int Timeout { get; set; } = 10000;

    /// <summary>
    ///     List of tools to expose, or "*" for all (default: "*")
    /// </summary>
    public IReadOnlyList<string>? Tools { get; set; } = new[] { "*" };

    /// <summary>
    ///     HTTP headers for HTTP servers
    /// </summary>
    public IReadOnlyDictionary<string, string>? Headers { get; set; }

    /// <summary>
    ///     Environment variables for stdio servers
    /// </summary>
    public IReadOnlyDictionary<string, string>? Env { get; set; }

    /// <summary>
    ///     Working directory for stdio servers
    /// </summary>
    public string? Cwd { get; set; }
}
