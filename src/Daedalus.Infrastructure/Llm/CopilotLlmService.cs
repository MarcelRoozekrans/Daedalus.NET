using System.Text;
using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Services;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.Logging;

namespace Daedalus.Infrastructure.Llm;

/// <summary>
///     LLM service implementation using GitHub Copilot SDK.
/// </summary>
public sealed partial class CopilotLlmService(
    CopilotClient client,
    string model,
    ILogger<CopilotLlmService> logger) : ILlmService, IAsyncDisposable
{
    private static readonly string[] _windowsTools = ["PowerShell", "Cmd"];
    private static readonly string[] _unixTools = ["Bash", "Shell"];
    private static readonly string[] _macTools = ["Bash", "Shell", "Zsh"];

    /// <summary>
    ///     Disposes the Copilot client.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (client.State == ConnectionState.Connected)
            {
                await client.StopAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            LogErrorStoppingCopilot(logger, ex);
        }

        await client.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public string ProviderName => "copilot";

    /// <inheritdoc />
    public bool SupportsSubagents => false;

    /// <summary>
    ///     Invokes GitHub Copilot with the given prompt.
    /// </summary>
    public async Task<Result<string>> InvokeAsync(string prompt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return Result.Failure<string>("Prompt cannot be empty");
        }

        try
        {
            // Ensure client is running
            if (client.State != ConnectionState.Connected)
            {
                await client.StartAsync().ConfigureAwait(false);
            }

            // Get OS-specific tools and create session configuration
            var osTools = GetOsSpecificTools();
            var sessionConfig = new SessionConfig { Model = model, Streaming = true, AvailableTools = osTools };

            // Create a session with OS-aware tool configuration
            await using var session = await client.CreateSessionAsync(
                sessionConfig,
                ct
            ).ConfigureAwait(false);

            var contentBuilder = new StringBuilder();
            var sessionCompleted = new TaskCompletionSource<Result<string>>();

            // Set up event handlers for the session
            session.On(evt =>
            {
                switch (evt)
                {
                    case AssistantMessageEvent msg:
                        contentBuilder.Append(msg.Data?.Content ?? string.Empty);
                        break;

                    case AssistantMessageDeltaEvent delta:
                        contentBuilder.Append(delta.Data?.DeltaContent ?? string.Empty);
                        break;

                    case SessionIdleEvent:
                        var content = contentBuilder.ToString().Trim();
                        if (string.IsNullOrEmpty(content))
                        {
                            sessionCompleted.SetResult(
                                Result.Failure<string>("Copilot returned empty response"));
                        }
                        else
                        {
                            sessionCompleted.SetResult(Result.Success(content));
                        }

                        break;

                    case SessionErrorEvent error:
                        LogSessionError(logger, error.Data?.Message);
                        sessionCompleted.SetResult(
                            Result.Failure<string>($"Copilot session error: {error.Data?.Message}"));
                        break;
                }
            });

            // Send the prompt
            await session.SendAsync(
                new MessageOptions { Prompt = prompt },
                ct
            ).ConfigureAwait(false);

            // Wait for completion with timeout
            var completionTask = await Task.WhenAny(
                sessionCompleted.Task,
                Task.Delay(TimeSpan.FromSeconds(30), ct)
            ).ConfigureAwait(false);

            if (completionTask == sessionCompleted.Task)
            {
                return await sessionCompleted.Task.ConfigureAwait(false);
            }

            LogInvocationTimedOut(logger);
            return Result.Failure<string>("Copilot invocation timed out");
        }
        catch (OperationCanceledException)
        {
            LogInvocationCancelled(logger);
            return Result.Failure<string>("Operation cancelled");
        }
        catch (Exception ex)
        {
            LogErrorInvokingCopilot(logger, ex, ex.Message);
            return Result.Failure<string>($"Error invoking Copilot: {ex.Message}");
        }
    }

    /// <summary>
    ///     Invokes GitHub Copilot with MCP server support.
    ///     Allows the LLM to access external tools and context via the Model Context Protocol.
    /// </summary>
    public async Task<Result<string>> InvokeWithMcpAsync(
        string prompt,
        McpIntegrationOptions mcpOptions,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return Result.Failure<string>("Prompt cannot be empty");
        }

        if (mcpOptions == null)
        {
            // Fall back to standard invocation if no MCP options
            return await InvokeAsync(prompt, ct).ConfigureAwait(false);
        }

        try
        {
            // Ensure client is running
            if (client.State != ConnectionState.Connected)
            {
                await client.StartAsync().ConfigureAwait(false);
            }

            // Get OS-specific tools and MCP servers
            var osTools = GetOsSpecificTools();
            var mcpServersDict = BuildMcpServerConfigurations(mcpOptions);

            LogMcpInvocationStarting(logger, prompt.Length, mcpServersDict.Count);

            var sessionConfig = new SessionConfig
            {
                Model = model,
                Streaming = true,
                AvailableTools = osTools,
                // Add MCP servers to session configuration
                McpServers = mcpServersDict.Count > 0 ? mcpServersDict : null
            };

            // Create a session with MCP support
            await using var session = await client.CreateSessionAsync(
                sessionConfig,
                ct
            ).ConfigureAwait(false);

            var contentBuilder = new StringBuilder();
            var sessionCompleted = new TaskCompletionSource<Result<string>>();

            // Set up event handlers for the session
            session.On(evt =>
            {
                switch (evt)
                {
                    case AssistantMessageEvent msg:
                        contentBuilder.Append(msg.Data?.Content ?? string.Empty);
                        break;

                    case AssistantMessageDeltaEvent delta:
                        contentBuilder.Append(delta.Data?.DeltaContent ?? string.Empty);
                        break;

                    case SessionIdleEvent:
                        var content = contentBuilder.ToString().Trim();
                        if (string.IsNullOrEmpty(content))
                        {
                            sessionCompleted.SetResult(
                                Result.Failure<string>("Copilot returned empty response"));
                        }
                        else
                        {
                            sessionCompleted.SetResult(Result.Success(content));
                        }

                        break;

                    case SessionErrorEvent error:
                        LogSessionError(logger, error.Data?.Message);
                        sessionCompleted.SetResult(
                            Result.Failure<string>($"Copilot session error: {error.Data?.Message}"));
                        break;
                }
            });

            // Send the prompt
            await session.SendAsync(
                new MessageOptions { Prompt = prompt },
                ct
            ).ConfigureAwait(false);

            // Wait for completion with timeout
            var completionTask = await Task.WhenAny(
                sessionCompleted.Task,
                Task.Delay(TimeSpan.FromSeconds(30), ct)
            ).ConfigureAwait(false);

            if (completionTask == sessionCompleted.Task)
            {
                LogMcpInvocationCompleted(logger);
                return await sessionCompleted.Task.ConfigureAwait(false);
            }

            LogInvocationTimedOut(logger);
            return Result.Failure<string>("Copilot invocation timed out");
        }
        catch (OperationCanceledException)
        {
            LogInvocationCancelled(logger);
            return Result.Failure<string>("Operation cancelled");
        }
        catch (Exception ex)
        {
            LogErrorInvokingCopilotWithMcp(logger, ex, ex.Message);
            return Result.Failure<string>($"Error invoking Copilot with MCP: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public Task<Result<SubagentResult>> InvokeSubagentAsync(
        string prompt,
        SubagentOptions options,
        CancellationToken ct)
    {
        return Task.FromResult(
            Result.Failure<SubagentResult>("GitHub Copilot does not support subagent invocations"));
    }

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<SubagentResult>>> InvokeParallelSubagentsAsync(
        IReadOnlyList<string> prompts,
        SubagentOptions options,
        int maxParallelism = 10,
        CancellationToken ct = default)
    {
        return Task.FromResult(
            Result.Failure<IReadOnlyList<SubagentResult>>(
                "GitHub Copilot does not support parallel subagent invocations"));
    }

    /// <summary>
    ///     Gets OS-specific available tools for the Copilot session.
    /// </summary>
    /// <returns>A list of tools available on the current operating system.</returns>
    private List<string> GetOsSpecificTools()
    {
        var tools = new List<string> { "Read", "Write", "Grep" };

        if (OperatingSystem.IsWindows())
        {
            // Windows-specific tools
            tools.AddRange(_windowsTools);
            LogDetectedPlatform(logger, "Windows");
        }
        else if (OperatingSystem.IsLinux())
        {
            // Linux-specific tools
            tools.AddRange(_unixTools);
            LogDetectedPlatform(logger, "Linux");
        }
        else if (OperatingSystem.IsMacOS())
        {
            // macOS-specific tools
            tools.AddRange(_macTools);
            LogDetectedPlatform(logger, "macOS");
        }

        return tools;
    }

    /// <summary>
    ///     Builds MCP server configurations dictionary for the Copilot session.
    /// </summary>
    private static Dictionary<string, object> BuildMcpServerConfigurations(McpIntegrationOptions mcpOptions)
    {
        var mcpServers = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        if (!mcpOptions.Enabled || mcpOptions.Servers.Count == 0)
        {
            return mcpServers;
        }

        foreach (var (serverName, serverConfig) in mcpOptions.Servers)
        {
            // Only add servers that are enabled and have a URL
            if (serverConfig != null && !string.IsNullOrEmpty(serverConfig.Url))
            {
                var config = new McpServerConfig
                {
                    Type = serverConfig.Type ?? "http",
                    Url = serverConfig.Url,
                    Command = serverConfig.Command,
                    Args = serverConfig.Args,
                    Timeout = serverConfig.Timeout,
                    Tools = serverConfig.Tools,
                    Headers = serverConfig.Headers,
                    Env = serverConfig.Env,
                    Cwd = serverConfig.Cwd
                };
                mcpServers[serverName] = config;
            }
        }

        return mcpServers;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Error stopping Copilot client")]
    private static partial void LogErrorStoppingCopilot(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Copilot session error: {Message}")]
    private static partial void LogSessionError(ILogger logger, string? message);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Copilot invocation timed out")]
    private static partial void LogInvocationTimedOut(ILogger logger);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Copilot invocation cancelled")]
    private static partial void LogInvocationCancelled(ILogger logger);

    [LoggerMessage(EventId = 5, Level = LogLevel.Error, Message = "Error invoking Copilot: {ExceptionMessage}")]
    private static partial void LogErrorInvokingCopilot(ILogger logger, Exception exception, string exceptionMessage);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "Detected platform: {Platform}")]
    private static partial void LogDetectedPlatform(ILogger logger, string platform);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information,
        Message = "MCP invocation starting: PromptLength={PromptLength}, ServerCount={ServerCount}")]
    private static partial void LogMcpInvocationStarting(ILogger logger, int promptLength, int serverCount);

    [LoggerMessage(EventId = 8, Level = LogLevel.Information, Message = "MCP invocation completed successfully")]
    private static partial void LogMcpInvocationCompleted(ILogger logger);

    [LoggerMessage(EventId = 9, Level = LogLevel.Error,
        Message = "Error invoking Copilot with MCP: {ExceptionMessage}")]
    private static partial void LogErrorInvokingCopilotWithMcp(ILogger logger, Exception exception,
        string exceptionMessage);
}
