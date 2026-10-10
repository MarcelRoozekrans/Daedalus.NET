using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Anthropic;
using Anthropic.Core;
using ZeroAlloc.Results;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Daedalus.Infrastructure.Agents;

/// <summary>
///     Creates and invokes LLM agents backed by the Microsoft Agent Framework with Anthropic Claude.
///     MCP tools are automatically attached to every agent instance via <see cref="McpToolBuilder" />.
/// </summary>
[SuppressMessage("Design", "CA1031:Do not catch general exception types",
    Justification = "LLM invocations should return Result failures, not throw")]
public sealed partial class AgentFactory : IAgentFactory
{
    private readonly string _apiKey;
    private readonly string _defaultModel;
    private readonly int _maxTokens;
    private readonly McpToolBuilder _mcpToolBuilder;
    private readonly McpIntegrationOptions _mcpOptions;
    private readonly ILogger<AgentFactory> _logger;

    public AgentFactory(
        IConfiguration configuration,
        McpToolBuilder mcpToolBuilder,
        McpIntegrationOptions mcpOptions,
        ILoggerFactory loggerFactory)
    {
        _mcpToolBuilder = mcpToolBuilder;
        _mcpOptions = mcpOptions;
        _logger = loggerFactory.CreateLogger<AgentFactory>();

        // Read Anthropic configuration
        var llmSection = configuration.GetSection("ExternalServices:Llm:Claude");
        _apiKey = llmSection["ApiKey"]
                  ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                  ?? string.Empty;
        _defaultModel = llmSection["Model"] ?? "claude-sonnet-4-20250514";
        _maxTokens = int.TryParse(
            llmSection["MaxTokens"], System.Globalization.CultureInfo.InvariantCulture, out var max)
            ? max
            : 8192;

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _logger.LogWarning(
                "Anthropic API key not configured. Set ExternalServices:Llm:Claude:ApiKey or ANTHROPIC_API_KEY env var");
        }
    }

    /// <inheritdoc />
    public async Task<Result<LlmInvocationResult>> InvokeAsync(string prompt, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return Result<LlmInvocationResult>.Failure("Prompt cannot be empty");
        }

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            return Result<LlmInvocationResult>.Failure("Anthropic API key is not configured");
        }

        try
        {
            LogInvocationStarting(_logger, prompt.Length, _defaultModel);

            // Create IChatClient from Anthropic provider
            var chatClient = CreateChatClient();

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
                LogEmptyResponse(_logger);
                return Result<LlmInvocationResult>.Failure("Claude returned empty response");
            }

            var (inputTokens, outputTokens) = ExtractTokenUsage(response);
            LogInvocationCompleted(_logger, text.Length);
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
            LogInvocationCancelled(_logger);
            return Result<LlmInvocationResult>.Failure("Operation cancelled");
        }
        catch (Exception ex)
        {
            LogErrorInvoking(_logger, ex, ex.Message);
            return Result<LlmInvocationResult>.Failure($"Error invoking Claude: {ex.Message}");
        }
    }


    /// <summary>
    ///     Creates an <see cref="IChatClient" /> backed by Anthropic Claude.
    ///     Uses the <c>Anthropic</c> SDK's <c>AsIChatClient()</c> extension
    ///     from <c>Microsoft.Extensions.AI</c>.
    /// </summary>
    private IChatClient CreateChatClient()
    {
        var anthropicClient = new AnthropicClient(new ClientOptions { ApiKey = _apiKey });

        // AsIChatClient() is an extension method from the Anthropic package
        // that creates an IChatClient wrapping the Anthropic API
        var chatClient = anthropicClient.AsIChatClient(_defaultModel, _maxTokens);

        // Wrap with FunctionInvokingChatClient for automatic MCP tool calling
        return new ChatClientBuilder(chatClient)
            .UseFunctionInvocation()
            .Build();
    }

    /// <summary>
    ///     Builds MCP tools from configured servers, if MCP is enabled.
    /// </summary>
    private async Task<IReadOnlyList<AITool>> BuildMcpToolsAsync(CancellationToken ct)
    {
        if (!_mcpOptions.Enabled || _mcpOptions.Servers.Count == 0)
        {
            return Array.Empty<AITool>();
        }

        return await _mcpToolBuilder.BuildToolsAsync(_mcpOptions.Servers, ct);
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
