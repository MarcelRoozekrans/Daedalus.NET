using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Daedalus.Application.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Server;

namespace Daedalus.Infrastructure.Agents;

/// <summary>
///     Converts <see cref="McpServerConfig" /> entries into <see cref="AITool" /> instances
///     by connecting to MCP servers and listing their available tools.
///     Tools are cached per server name to avoid reconnecting on every agent creation.
/// </summary>
/// <remarks>
///     <c>McpClientTool</c> extends <c>AIFunction</c> (which extends <c>AITool</c>),
///     so MCP tools can be used directly anywhere <c>AITool</c> is accepted
///     (e.g., <c>ChatClientAgent</c> constructor, <c>ChatOptions.Tools</c>).
/// </remarks>
[SuppressMessage("Design", "CA1031:Do not catch general exception types",
    Justification = "MCP server connection failures should be logged and skipped, not thrown")]
public sealed partial class McpToolBuilder(
    IServiceProvider serviceProvider,
    ILogger<McpToolBuilder> logger) : IAsyncDisposable
{
    private readonly Dictionary<string, McpClient> _clients = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<AITool>> _toolCache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IServiceScope? _localScope;
    private bool _disposed;

    /// <summary>
    ///     Builds AITool instances from a dictionary of MCP server configurations.
    ///     Connects to each server, lists tools, and caches results.
    /// </summary>
    /// <param name="servers">MCP server configurations keyed by name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Flat list of all tools from all configured servers.</returns>
    public async Task<IReadOnlyList<AITool>> BuildToolsAsync(
        IReadOnlyDictionary<string, McpServerConfig> servers,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (servers.Count == 0)
        {
            return Array.Empty<AITool>();
        }

        var allTools = new List<AITool>();

        foreach (var (serverName, serverConfig) in servers)
        {
            var tools = await GetOrConnectServerToolsAsync(serverName, serverConfig, ct);
            allTools.AddRange(tools);
        }

        LogToolsBuildCompleted(logger, allTools.Count, servers.Count);
        return allTools;
    }

    /// <summary>
    ///     Gets cached tools or connects to the MCP server and lists them.
    /// </summary>
    private async Task<IReadOnlyList<AITool>> GetOrConnectServerToolsAsync(
        string serverName,
        McpServerConfig serverConfig,
        CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            // Return cached tools if already connected
            if (_toolCache.TryGetValue(serverName, out var cached))
            {
                return cached;
            }

            // Handle local (in-process) tool type — resolve tools from DI
            if (serverConfig.Type.Equals("local", StringComparison.OrdinalIgnoreCase))
            {
                var localTools = BuildLocalTools(serverName);
                _toolCache[serverName] = localTools;
                LogServerConnected(logger, serverName, localTools.Count);
                return localTools;
            }

            // Create transport based on server type
            var transport = CreateTransport(serverName, serverConfig);
            if (transport is null)
            {
                LogSkippingServer(logger, serverName, serverConfig.Type);
                return Array.Empty<AITool>();
            }

            // Connect to the MCP server
            LogConnectingToServer(logger, serverName, serverConfig.Type);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(serverConfig.Timeout);

            var client = await McpClient.CreateAsync(
                transport,
                cancellationToken: timeoutCts.Token);

            // List all tools from the server
            var mcpTools = await client.ListToolsAsync(cancellationToken: timeoutCts.Token);

            // McpClientTool extends AIFunction which extends AITool — direct cast
            var tools = mcpTools.Cast<AITool>().ToList();

            // Cache the client and tools
            _clients[serverName] = client;
            _toolCache[serverName] = tools;

            LogServerConnected(logger, serverName, tools.Count);
            return tools;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Propagate caller cancellation
        }
        catch (Exception ex)
        {
            LogServerConnectionFailed(logger, ex, serverName, ex.Message);
            return Array.Empty<AITool>();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    ///     Creates the appropriate MCP transport based on server configuration.
    /// </summary>
    private static IClientTransport? CreateTransport(string serverName, McpServerConfig config)
    {
        return config.Type.ToUpperInvariant() switch
        {
            "STDIO" when !string.IsNullOrEmpty(config.Command) => new StdioClientTransport(
                new StdioClientTransportOptions
                {
                    Name = serverName,
                    Command = config.Command,
                    Arguments = config.Args?.ToList(),
                    EnvironmentVariables = config.Env?.ToDictionary(
                        kvp => kvp.Key,
                        kvp => (string?)kvp.Value,
                        StringComparer.Ordinal),
                    WorkingDirectory = config.Cwd
                }),

            "HTTP" or "SSE" when !string.IsNullOrEmpty(config.Url) => new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri(config.Url),
                    Name = serverName
                }),

            _ => null // Unsupported transport type
        };
    }

    /// <summary>
    ///     Builds tools from in-process <see cref="McpServerToolTypeAttribute" /> classes resolved via DI.
    ///     Discovers classes annotated with [McpServerToolType] in the Infrastructure assembly,
    ///     then creates <see cref="AIFunction" /> instances for each [McpServerTool]-annotated method.
    /// </summary>
#pragma warning disable IL2026 // Members annotated with RequiresUnreferencedCodeAttribute
#pragma warning disable IL2072 // Target parameter of ActivatorUtilities.CreateInstance
#pragma warning disable IL2075 // Target parameter of Type.GetMethods
    private List<AITool> BuildLocalTools(string serverName)
    {
        var tools = new List<AITool>();

        // Create a scope for resolving scoped services (e.g., repositories needing DbContext).
        // The scope is stored as a field and disposed with the McpToolBuilder instance.
        _localScope = serviceProvider.CreateScope();
        var scopedProvider = _localScope.ServiceProvider;

        // Discover tool classes from the Infrastructure assembly
        var toolTypes = GetType().Assembly
            .GetTypes()
            .Where(t => Attribute.IsDefined(t, typeof(McpServerToolTypeAttribute))
                         && !t.IsAbstract);

        foreach (var toolType in toolTypes)
        {
            try
            {
                var instance = ActivatorUtilities.CreateInstance(scopedProvider, toolType);

                // Discover public methods annotated with [McpServerTool]
                var methods = toolType
                    .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public)
                    .Where(m => Attribute.IsDefined(m, typeof(McpServerToolAttribute)));

                foreach (var method in methods)
                {
                    var toolAttr = method.GetCustomAttribute<McpServerToolAttribute>()!;
                    var name = toolAttr.Name ?? method.Name;
                    var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description;

                    var aiFunction = AIFunctionFactory.Create(method, instance, name, description);
                    tools.Add(aiFunction);
                }
            }
            catch (Exception ex)
            {
                LogServerConnectionFailed(logger, ex, serverName,
                    $"Failed to create local tool from {toolType.Name}: {ex.Message}");
            }
        }

        return tools;
    }
#pragma warning restore IL2075
#pragma warning restore IL2072
#pragma warning restore IL2026

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var (name, client) in _clients)
        {
            try
            {
                await client.DisposeAsync();
            }
            catch (Exception ex)
            {
                LogClientDisposalError(logger, ex, name);
            }
        }

        _clients.Clear();
        _toolCache.Clear();
        _localScope?.Dispose();
        _lock.Dispose();
    }

    // ============== Logging Methods ==============

    [LoggerMessage(EventId = 300, Level = LogLevel.Information,
        Message = "MCP tools build completed: {ToolCount} tools from {ServerCount} servers")]
    private static partial void LogToolsBuildCompleted(ILogger logger, int toolCount, int serverCount);

    [LoggerMessage(EventId = 301, Level = LogLevel.Information,
        Message = "Connecting to MCP server '{ServerName}' ({ServerType})")]
    private static partial void LogConnectingToServer(ILogger logger, string serverName, string serverType);

    [LoggerMessage(EventId = 302, Level = LogLevel.Information,
        Message = "MCP server '{ServerName}' connected: {ToolCount} tools available")]
    private static partial void LogServerConnected(ILogger logger, string serverName, int toolCount);

    [LoggerMessage(EventId = 303, Level = LogLevel.Warning,
        Message = "Skipping MCP server '{ServerName}': unsupported type '{ServerType}' or missing configuration")]
    private static partial void LogSkippingServer(ILogger logger, string serverName, string serverType);

    [LoggerMessage(EventId = 304, Level = LogLevel.Error,
        Message = "Failed to connect to MCP server '{ServerName}': {ErrorMessage}")]
    private static partial void LogServerConnectionFailed(ILogger logger, Exception exception, string serverName,
        string errorMessage);

    [LoggerMessage(EventId = 305, Level = LogLevel.Warning,
        Message = "Error disposing MCP client for server '{ServerName}'")]
    private static partial void LogClientDisposalError(ILogger logger, Exception exception, string serverName);
}
