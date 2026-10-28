using System.Diagnostics.CodeAnalysis;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Services;
using Daedalus.Infrastructure.Agents;
using Daedalus.Infrastructure.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Thalos;
using ZeroAlloc.Results;

namespace Daedalus.Agents.Llm;

/// <summary>
///     Creates and invokes LLM agents backed by the chat client Thalos provides.
///     MCP tools are automatically attached to every agent instance via <see cref="McpToolBuilder" />.
/// </summary>
[SuppressMessage("Design", "CA1031:Do not catch general exception types",
    Justification = "LLM invocations should return Result failures, not throw")]
public sealed partial class AgentFactory(
    IChatClientProvider chatClientProvider,
    IOptions<ClaudeConfiguration> claude,
    McpToolBuilder mcpToolBuilder,
    McpIntegrationOptions mcpOptions,
    ILogger<AgentFactory> logger) : IAgentFactory
{
    // Validated non-blank when the options are read; see AddAgentFrameworkServices.
    private readonly string _defaultModel = claude.Value.Model!;
    private readonly int _maxTokens = claude.Value.MaxTokens;

    /// <inheritdoc />
    public async Task<Result<LlmInvocationResult>> InvokeAsync(string prompt, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return Result<LlmInvocationResult>.Failure("Prompt cannot be empty");
        }

        try
        {
            LogInvocationStarting(logger, prompt.Length, _defaultModel);

            // The provider throws InvalidOperationException here when no API key is set; the catch below turns it
            // into a failure result that names Thalos:Anthropic:ApiKey.
            using var chatClient = CreateChatClient();

            // Build MCP tools if enabled
            var tools = await BuildMcpToolsAsync(ct);

            // Build the request
            var messages = new List<ChatMessage>
            {
                new(ChatRole.User, prompt)
            };

            var options = new ChatOptions
            {
                ModelId = _defaultModel,
                MaxOutputTokens = _maxTokens,
                Tools = tools.Count > 0 ? tools.ToList() : null
            };

            var response = await chatClient.GetResponseAsync(messages, options, ct);
            var text = response.Text?.Trim();

            if (string.IsNullOrEmpty(text))
            {
                LogEmptyResponse(logger);
                return Result<LlmInvocationResult>.Failure("Claude returned empty response");
            }

            var (inputTokens, outputTokens) = ExtractTokenUsage(response);
            LogInvocationCompleted(logger, text.Length);
            return Result<LlmInvocationResult>.Success(new LlmInvocationResult
            {
                Response = text,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                ModelId = _defaultModel
            });
        }
        catch (OperationCanceledException)
        {
            LogInvocationCancelled(logger);
            return Result<LlmInvocationResult>.Failure("Operation cancelled");
        }
        catch (Exception ex)
        {
            LogErrorInvoking(logger, ex, ex.Message);
            return Result<LlmInvocationResult>.Failure($"Error invoking Claude: {ex.Message}");
        }
    }

    /// <summary>
    ///     Asks Thalos's <see cref="IChatClientProvider" /> for a chat client and wraps it with function invocation, so
    ///     MCP tool calls run automatically. The caller owns and disposes the client.
    /// </summary>
    private IChatClient CreateChatClient()
    {
        var agent = new AgentDefinition
        {
            Id = AgentId.New(),
            Name = "agent-factory",
            Instructions = "",
            Model = _defaultModel,
            MaxOutputTokens = _maxTokens
        };

        return new ChatClientBuilder(chatClientProvider.CreateChatClient(agent))
            .UseFunctionInvocation()
            .Build();
    }

    /// <summary>
    ///     Builds MCP tools from configured servers, if MCP is enabled.
    /// </summary>
    private async Task<IReadOnlyList<AITool>> BuildMcpToolsAsync(CancellationToken ct)
    {
        if (!mcpOptions.Enabled || mcpOptions.Servers.Count == 0)
        {
            return Array.Empty<AITool>();
        }

        return await mcpToolBuilder.BuildToolsAsync(mcpOptions.Servers, ct);
    }

    /// <summary>
    ///     Extracts token usage from a chat response.
    /// </summary>
    private static (int inputTokens, int outputTokens) ExtractTokenUsage(ChatResponse response)
    {
        var inputTokens = 0;
        var outputTokens = 0;

        if (response.Usage is { } usage)
        {
            inputTokens = (int)(usage.InputTokenCount ?? 0);
            outputTokens = (int)(usage.OutputTokenCount ?? 0);
        }

        return (inputTokens, outputTokens);
    }

    // ============== Logging Methods ==============

    [LoggerMessage(EventId = 400, Level = LogLevel.Information,
        Message = "Agent invocation starting: PromptLength={PromptLength}, Model={Model}")]
    private static partial void LogInvocationStarting(ILogger logger, int promptLength, string model);

    [LoggerMessage(EventId = 401, Level = LogLevel.Information,
        Message = "Agent invocation completed: ResponseLength={ResponseLength}")]
    private static partial void LogInvocationCompleted(ILogger logger, int responseLength);

    [LoggerMessage(EventId = 402, Level = LogLevel.Warning, Message = "Agent returned empty response")]
    private static partial void LogEmptyResponse(ILogger logger);

    [LoggerMessage(EventId = 403, Level = LogLevel.Information, Message = "Agent invocation cancelled")]
    private static partial void LogInvocationCancelled(ILogger logger);

    [LoggerMessage(EventId = 404, Level = LogLevel.Error, Message = "Error invoking agent: {ExceptionMessage}")]
    private static partial void LogErrorInvoking(ILogger logger, Exception exception, string exceptionMessage);
}
