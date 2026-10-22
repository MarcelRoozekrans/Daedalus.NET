using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Daedalus.Application.Services;
using Daedalus.Infrastructure.Agents.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

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
///     <para>
///         Local (in-process) tools create a fresh DI scope on each invocation, preventing stale
///         <c>DbContext</c> references that would otherwise accumulate in a singleton's lifetime.
///     </para>
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

            // Handle local (in-process) tool type — resolve tools from DI.
            // Local tools are NOT cached because they need fresh DI scopes per invocation
            // (DbContext, repositories, etc. are scoped services that would go stale in a singleton cache).
            if (serverConfig.Type.Equals("local", StringComparison.OrdinalIgnoreCase))
            {
                var localTools = BuildLocalTools(serverName);
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
    ///     M.E.AI's default options with <see cref="McpToolsJsonContext" /> in front of M.E.AI's own resolver, so the
    ///     tools' parameter and return types come from generated metadata while the encoder and naming stay M.E.AI's.
    /// </summary>
    private static readonly JsonSerializerOptions ToolSerializerOptions = CreateToolSerializerOptions();

    /// <summary>
    ///     The in-process tool classes, in the order the assembly scan found them before phase 3.2. Each entry builds its
    ///     class's tools for a root provider; <c>McpToolRegistryGuardTests</c> keeps the list complete and holds it to the
    ///     reflective registration it replaced.
    /// </summary>
    internal static readonly IReadOnlyList<(Type ToolType, Func<IServiceProvider, IEnumerable<AIFunction>> Create)> LocalTools =
    [
        (typeof(DaedalusFailurePatternsTools), CreateFailurePatternsTools),
        (typeof(DaedalusLearningsTools), CreateLearningsTools),
    ];

    /// <summary>
    ///     Builds the in-process tools from <see cref="LocalTools" />. A class whose dependencies cannot be resolved is
    ///     logged and left out, as before. Each tool invocation creates a fresh DI scope so that scoped services
    ///     (DbContext, repositories) are never stale.
    /// </summary>
    private List<AITool> BuildLocalTools(string serverName)
    {
        var tools = new List<AITool>();

        foreach (var (toolType, create) in LocalTools)
        {
            try
            {
                tools.AddRange(create(serviceProvider));
            }
            catch (Exception ex)
            {
                LogServerConnectionFailed(logger, ex, serverName,
                    $"Failed to create local tool from {toolType.Name}: {ex.Message}");
            }
        }

        return tools;
    }

    private static AIFunction[] CreateFailurePatternsTools(IServiceProvider rootProvider)
    {
        Probe<DaedalusFailurePatternsTools>(rootProvider);
        return
        [
            AIFunctionFactory.Create(
                async (
                    [Description(DaedalusFailurePatternsTools.ErrorMessageDescription)] string errorMessage,
                    [Description(DaedalusFailurePatternsTools.MaxResultsDescription)]
                    int maxResults = DaedalusFailurePatternsTools.DefaultMaxResults,
                    CancellationToken cancellationToken = default) =>
                {
                    await using var scope = rootProvider.CreateAsyncScope();
                    var tools = ActivatorUtilities.CreateInstance<DaedalusFailurePatternsTools>(scope.ServiceProvider);
                    return await tools.SearchFailurePatterns(errorMessage, maxResults, cancellationToken)
                        .ConfigureAwait(false);
                },
                Options(DaedalusFailurePatternsTools.ToolName, DaedalusFailurePatternsTools.ToolDescription)),
        ];
    }

    private static AIFunction[] CreateLearningsTools(IServiceProvider rootProvider)
    {
        Probe<DaedalusLearningsTools>(rootProvider);
        return
        [
            AIFunctionFactory.Create(
                async (
                    [Description(DaedalusLearningsTools.QueryDescription)] string query,
                    [Description(DaedalusLearningsTools.MaxResultsDescription)]
                    int maxResults = DaedalusLearningsTools.DefaultMaxResults,
                    CancellationToken cancellationToken = default) =>
                {
                    await using var scope = rootProvider.CreateAsyncScope();
                    var tools = ActivatorUtilities.CreateInstance<DaedalusLearningsTools>(scope.ServiceProvider);
                    return await tools.SearchLearnings(query, maxResults, cancellationToken).ConfigureAwait(false);
                },
                Options(DaedalusLearningsTools.ToolName, DaedalusLearningsTools.ToolDescription)),
        ];
    }

    /// <summary>
    ///     Constructs a throw-away instance in a throw-away scope, so a class whose dependencies are missing fails when
    ///     the tools are built, not when a model calls one.
    /// </summary>
    private static void Probe<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(
        IServiceProvider rootProvider)
    {
        using var probeScope = rootProvider.CreateScope();
        _ = ActivatorUtilities.CreateInstance<T>(probeScope.ServiceProvider);
    }

    private static AIFunctionFactoryOptions Options(string name, string description) => new()
    {
        Name = name,
        Description = description,
        SerializerOptions = ToolSerializerOptions,
    };

    private static JsonSerializerOptions CreateToolSerializerOptions()
    {
        var options = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions)
        {
            TypeInfoResolver = JsonTypeInfoResolver.Combine(
                McpToolsJsonContext.Default, AIJsonUtilities.DefaultOptions.TypeInfoResolver),
        };
        options.MakeReadOnly();
        return options;
    }

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
