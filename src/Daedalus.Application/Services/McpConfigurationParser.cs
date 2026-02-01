using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Daedalus.Application.Services;

/// <summary>
///     AOT-compatible parser for MCP configuration from <see cref="IConfiguration" />.
///     Single source of truth — used by both RalphLoopService and GeneratePrdCommandHandler.
/// </summary>
public static class McpConfigurationParser
{
    /// <summary>
    ///     Extracts MCP integration options from the <c>ExternalServices:Mcp</c> configuration section.
    ///     Falls back to <c>RalphLoop:MCP</c> for backward compatibility.
    /// </summary>
    public static McpIntegrationOptions Extract(IConfiguration? configuration)
    {
        if (configuration is null)
        {
            return new McpIntegrationOptions { Enabled = false };
        }

        // Primary source: ExternalServices:Mcp (registered via options pattern)
        var result = TryExtractFromSection(configuration.GetSection("ExternalServices:Mcp"));
        if (result is not null)
        {
            return result;
        }

        // Legacy fallback: RalphLoop:MCP
        result = TryExtractFromSection(configuration.GetSection("RalphLoop:MCP"));
        if (result is not null)
        {
            return result;
        }

        return new McpIntegrationOptions { Enabled = false };
    }

    private static McpIntegrationOptions? TryExtractFromSection(IConfigurationSection section)
    {
        try
        {
            if (!section.Exists())
            {
                return null;
            }

            var enabled = bool.TryParse(section["Enabled"], out var e) && e;

            var serversSection = section.GetSection("Servers");
            var serverDict = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal);

            if (serversSection.Exists())
            {
                foreach (var serverSection in serversSection.GetChildren())
                {
                    var serverName = serverSection.Key;
                    var serverConfig = new McpServerConfig
                    {
                        Type = serverSection["Type"] ?? "http",
                        Url = serverSection["Url"],
                        Command = serverSection["Command"],
                        Timeout = int.TryParse(
                            serverSection["Timeout"], CultureInfo.InvariantCulture, out var timeout)
                            ? timeout
                            : 10000
                    };

                    // Parse Args array
                    var argsSection = serverSection.GetSection("Args");
                    if (argsSection.Exists())
                    {
                        var args = new List<string>();
                        foreach (var argSection in argsSection.GetChildren())
                        {
                            var value = argSection.Value ?? string.Empty;
                            if (value.Length > 0)
                            {
                                args.Add(value);
                            }
                        }

                        if (args.Count > 0)
                        {
                            serverConfig.Args = [.. args];
                        }
                    }

                    // Parse Tools array
                    var toolsSection = serverSection.GetSection("Tools");
                    if (toolsSection.Exists())
                    {
                        var tools = new List<string>();
                        foreach (var toolSection in toolsSection.GetChildren())
                        {
                            var value = toolSection.Value ?? string.Empty;
                            if (value.Length > 0)
                            {
                                tools.Add(value);
                            }
                        }

                        if (tools.Count > 0)
                        {
                            serverConfig.Tools = [.. tools];
                        }
                    }

                    // Extract headers
                    var headersSection = serverSection.GetSection("Headers");
                    if (headersSection.Exists())
                    {
                        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var header in headersSection.GetChildren())
                        {
                            headers[header.Key] = header.Value ?? string.Empty;
                        }

                        if (headers.Count > 0)
                        {
                            serverConfig.Headers = headers;
                        }
                    }

                    serverDict[serverName] = serverConfig;
                }
            }

            return new McpIntegrationOptions { Enabled = enabled, Servers = serverDict };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to extract MCP configuration: {ex.Message}");
            return null;
        }
    }
}
