using Daedalus.Application.Services;

namespace Daedalus.Infrastructure.Configuration;

/// <summary>
///     The <c>ExternalServices</c> section's MCP integration. Its other keys are bound by their own options:
///     <c>Llm:Claude</c> by <see cref="ClaudeConfiguration"/>, and <c>Platforms:GitHub</c> by <c>GitHubOptions</c>.
///     Context7 documentation is available via MCP server tools configured in the Mcp section.
/// </summary>
public sealed class ExternalServicesConfiguration
{
    public const string SectionName = "ExternalServices";

    /// <summary>
    ///     MCP (Model Context Protocol) integration configuration.
    ///     Includes Context7 documentation server (resolve-library-id, get-library-docs tools).
    /// </summary>
    public McpIntegrationOptions Mcp { get; set; } = new();
}
