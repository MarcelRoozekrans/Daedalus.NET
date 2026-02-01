using Daedalus.Application.Services;

namespace Daedalus.Infrastructure.Configuration;

/// <summary>
///     Configuration for external service integrations
/// </summary>
public sealed class ExternalServicesConfiguration
{
    public const string SectionName = "ExternalServices";

    /// <summary>
    ///     MCP (Model Context Protocol) integration configuration
    /// </summary>
    public McpIntegrationOptions Mcp { get; set; } = new();

    /// <summary>
    ///     Context7 documentation service configuration
    /// </summary>
    public Context7Configuration Context7 { get; set; } = new();

    /// <summary>
    ///     Repository platform configurations
    /// </summary>
    public RepositoryPlatformsConfiguration Platforms { get; set; } = new();

    /// <summary>
    ///     LLM service configuration
    /// </summary>
    public LlmServiceConfiguration Llm { get; set; } = new();
}
