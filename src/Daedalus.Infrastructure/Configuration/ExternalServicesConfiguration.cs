using Daedalus.Application.Services;

namespace Daedalus.Infrastructure.Configuration;

/// <summary>
///     Configuration for external service integrations.
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

    /// <summary>
    ///     Repository platform configurations
    /// </summary>
    public RepositoryPlatformsConfiguration Platforms { get; set; } = new();

    /// <summary>
    ///     LLM service configuration
    /// </summary>
    public LlmServiceConfiguration Llm { get; set; } = new();
}
