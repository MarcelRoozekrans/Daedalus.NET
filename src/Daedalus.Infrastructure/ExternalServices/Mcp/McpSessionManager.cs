using System.Diagnostics.CodeAnalysis;
using System.Text;
using Daedalus.Application.Services;
using Microsoft.Extensions.Logging;

namespace Daedalus.Infrastructure.ExternalServices.Mcp;

#pragma warning disable MA0002
/// <summary>
///     Manages Copilot SDK sessions with MCP server configurations.
/// </summary>
[SuppressMessage("Globalization", "CA1305")]
public sealed class McpSessionManager(ILogger<McpSessionManager> logger)
{
    /// <summary>
    ///     Creates and configures MCP servers from the provided configuration.
    /// </summary>
    public IReadOnlyDictionary<string, object> ConfigureMcpServers(McpIntegrationOptions mcpConfig)
    {
        var result = new Dictionary<string, object>();

        foreach (var (serverName, config) in mcpConfig.Servers)
        {
            var serverConfig = new Dictionary<string, object> { { "type", config.Type } };

            // Add tools configuration
            if (config.Tools != null && config.Tools.Count > 0)
            {
                serverConfig["tools"] = config.Tools;
            }

            // Type-specific configuration
            switch (config.Type)
            {
                case "http" or "sse":
                    if (!string.IsNullOrEmpty(config.Url))
                    {
                        serverConfig["url"] = config.Url;
                    }

                    if (config.Headers?.Count > 0)
                    {
                        serverConfig["headers"] = config.Headers;
                    }

                    break;

                case "local" or "stdio":
                    if (!string.IsNullOrEmpty(config.Command))
                    {
                        serverConfig["command"] = config.Command;
                    }

                    if (config.Args?.Count > 0)
                    {
                        serverConfig["args"] = config.Args;
                    }

                    if (config.Env?.Count > 0)
                    {
                        serverConfig["env"] = config.Env;
                    }

                    if (!string.IsNullOrEmpty(config.Cwd))
                    {
                        serverConfig["cwd"] = config.Cwd;
                    }

                    break;
            }

            if (config.Timeout > 0)
            {
                serverConfig["timeout"] = config.Timeout;
            }

            result[serverName] = serverConfig;
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Configured {ServerCount} MCP servers: {ServerNames}",
                result.Count,
#pragma warning disable MA0002
                string.Join(", ", result.Keys)
#pragma warning restore MA0002
            );
        }

        return result;
    }

    /// <summary>
    ///     Builds a system prompt that instructs the LLM about available MCP tools.
    /// </summary>
    public static string BuildSystemPromptForMcp(McpIntegrationOptions options)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You have access to the following MCP (Model Context Protocol) servers:");
        sb.AppendLine();

        foreach (var (serverName, config) in options.Servers)
        {
            sb.AppendLine($"## {serverName}");
            sb.AppendLine($"Type: {config.Type}");
            if (!string.IsNullOrEmpty(config.Url))
            {
                sb.AppendLine($"Endpoint: {config.Url}");
            }

            var toolsDisplay = config.Tools != null && config.Tools.Count > 0
#pragma warning disable MA0002
                ? string.Join(", ", config.Tools)
#pragma warning restore MA0002
                : "*";
            sb.AppendLine($"Available tools: {toolsDisplay}");
            sb.AppendLine();
        }

        sb.AppendLine("When solving the task:");
        sb.AppendLine("1. Use Context7 to fetch current library documentation when generating code");
        sb.AppendLine("2. Use Awesome Copilot to search for relevant prompts/instructions that match the task");
        sb.AppendLine("3. Apply relevant instructions from Awesome Copilot before generating solutions");
        sb.AppendLine("4. Always verify your code against the current APIs from Context7 documentation");

        return sb.ToString();
    }

    /// <summary>
    ///     Validates MCP configuration and logs warnings for potential issues.
    /// </summary>
    public void ValidateMcpConfiguration(McpIntegrationOptions options)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("MCP integration is disabled");
            return;
        }

        if (options.Servers.Count == 0)
        {
            logger.LogWarning("MCP is enabled but no servers are configured");
            return;
        }

        foreach (var (serverName, config) in options.Servers)
        {
            // Validate server-specific configuration
            switch (config.Type)
            {
                case "http" or "sse" when string.IsNullOrEmpty(config.Url):
                    logger.LogWarning("MCP server '{ServerName}' has type '{Type}' but no URL configured",
                        serverName, config.Type);
                    break;

                case "stdio" or "local" when string.IsNullOrEmpty(config.Command):
                    logger.LogWarning("MCP server '{ServerName}' has type '{Type}' but no command configured",
                        serverName, config.Type);
                    break;

                case "http" or "sse" or "stdio" or "local":
                    if (logger.IsEnabled(LogLevel.Debug))
                    {
                        logger.LogDebug("MCP server '{ServerName}' configured with type '{Type}'",
                            serverName, config.Type);
                    }

                    break;

                default:
                    logger.LogWarning("MCP server '{ServerName}' has unknown type '{Type}'",
                        serverName, config.Type);
                    break;
            }

            // Validate tools configuration
            if ((config.Tools == null || config.Tools.Count == 0) && logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("MCP server '{ServerName}' will expose all available tools", serverName);
            }
        }
    }
}
#pragma warning restore MA0002
