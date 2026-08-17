using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Anthropic;
using Anthropic.Core;
using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Daedalus.Infrastructure.Agents;

/// <summary>
///     Creates and invokes LLM agents backed by the Microsoft Agent Framework with Anthropic Claude.
///     MCP tools are automatically attached to every agent instance via <see cref="McpToolBuilder" />.
///     Supports the Ralph Wiggum subagent pattern: primary context as scheduler, subagents for expensive work.
/// </summary>
[SuppressMessage("Design", "CA1031:Do not catch general exception types",
    Justification = "LLM invocations should return Result failures, not throw")]
public sealed partial class RalphAgentFactory : IRalphAgentFactory
{
    private readonly string _apiKey;
    private readonly string _defaultModel;
    private readonly int _maxTokens;
    private readonly McpToolBuilder _mcpToolBuilder;
    private readonly McpIntegrationOptions _mcpOptions;
    private readonly ILogger<RalphAgentFactory> _logger;

    public RalphAgentFactory(
        IConfiguration configuration,
        McpToolBuilder mcpToolBuilder,
        McpIntegrationOptions mcpOptions,
        ILoggerFactory loggerFactory)
    {
        _mcpToolBuilder = mcpToolBuilder;
        _mcpOptions = mcpOptions;
        _logger = loggerFactory.CreateLogger<RalphAgentFactory>();

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
            return Result.Failure<LlmInvocationResult>("Prompt cannot be empty");
        }

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            return Result.Failure<LlmInvocationResult>("Anthropic API key is not configured");
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
                return Result.Failure<LlmInvocationResult>("Claude returned empty response");
            }

            var (inputTokens, outputTokens) = ExtractTokenUsage(response);
            LogInvocationCompleted(_logger, text.Length);
            return Result.Success(new LlmInvocationResult
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
            return Result.Failure<LlmInvocationResult>("Operation cancelled");
        }
        catch (Exception ex)
        {
            LogErrorInvoking(_logger, ex, ex.Message);
            return Result.Failure<LlmInvocationResult>($"Error invoking Claude: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<Result<SubagentResult>> InvokeSubagentAsync(
        string prompt,
        SubagentOptions options,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return Result.Failure<SubagentResult>("Subagent prompt cannot be empty");
        }

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            return Result.Failure<SubagentResult>("Anthropic API key is not configured");
        }

        var label = options.Label ?? "unnamed";

        try
        {
            LogSubagentStarting(_logger, label, prompt.Length);
            var stopwatch = Stopwatch.StartNew();

            // Create isolated chat client for subagent
            var chatClient = CreateChatClient();

            var messages = new List<ChatMessage>();

            // Add system prompt if provided
            if (!string.IsNullOrWhiteSpace(options.SystemPrompt))
            {
                messages.Add(new ChatMessage(ChatRole.System, options.SystemPrompt));
            }

            messages.Add(new ChatMessage(ChatRole.User, prompt));

            var modelToUse = options.ModelOverride ?? _defaultModel;

            var chatOptions = new ChatOptions
            {
                ModelId = modelToUse,
                MaxOutputTokens = options.MaxTokens
            };

            // Use subagent-specific timeout
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(options.Timeout);

            var response = await chatClient.GetResponseAsync(messages, chatOptions, timeoutCts.Token);

            stopwatch.Stop();
            var text = response.Text?.Trim();

            if (string.IsNullOrEmpty(text))
            {
                LogSubagentEmptyResponse(_logger, label);
                return Result.Failure<SubagentResult>($"Subagent '{label}' returned empty response");
            }

            // Extract token usage from response
            var (inputTokens, outputTokens) = ExtractTokenUsage(response);

            var result = new SubagentResult
            {
                Response = text,
                Label = label,
                Duration = stopwatch.Elapsed,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                UsedExtendedThinking = false,
                ThinkingContent = null
            };

            LogSubagentCompleted(_logger, label, stopwatch.Elapsed.TotalMilliseconds, inputTokens, outputTokens);
            return Result.Success(result);
        }
        catch (OperationCanceledException)
        {
            LogSubagentCancelled(_logger, label);
            return Result.Failure<SubagentResult>($"Subagent '{label}' was cancelled");
        }
        catch (Exception ex)
        {
            LogSubagentError(_logger, ex, label, ex.Message);
            return Result.Failure<SubagentResult>($"Subagent '{label}' failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<SubagentResult>>> RunParallelSubagentsAsync(
        IReadOnlyList<string> prompts,
        SubagentOptions options,
        int maxParallelism = 10,
        CancellationToken ct = default)
    {
        if (prompts.Count == 0)
        {
            return Result.Success<IReadOnlyList<SubagentResult>>(Array.Empty<SubagentResult>());
        }

        LogParallelSubagentsStarting(_logger, prompts.Count, maxParallelism);
        var stopwatch = Stopwatch.StartNew();

        var semaphore = new SemaphoreSlim(maxParallelism);
        var tasks = new Task<Result<SubagentResult>>[prompts.Count];

        // Task.WhenAll guarantees all tasks complete before continuing, so semaphore
        // disposal in the finally block is safe — all tasks will have released it.
#pragma warning disable CA2025 // Task.WhenAll ensures all tasks complete before semaphore disposal
        for (var i = 0; i < prompts.Count; i++)
        {
            var index = i;
            var subagentOptions = new SubagentOptions
            {
                SystemPrompt = options.SystemPrompt,
                MaxTokens = options.MaxTokens,
                Timeout = options.Timeout,
                EnableExtendedThinking = options.EnableExtendedThinking,
                ThinkingBudgetTokens = options.ThinkingBudgetTokens,
                ModelOverride = options.ModelOverride,
                Label = options.Label != null ? $"{options.Label}[{index}]" : $"subagent[{index}]"
            };

            tasks[index] = ExecuteWithSemaphore(semaphore, prompts[index], subagentOptions, ct);
        }
#pragma warning restore CA2025

        try
        {
            var results = await Task.WhenAll(tasks);
            stopwatch.Stop();

            var successResults = new List<SubagentResult>(prompts.Count);
            var failures = new List<string>();

            for (var i = 0; i < results.Length; i++)
            {
                if (results[i].IsSuccess)
                {
                    successResults.Add(results[i].Value);
                }
                else
                {
                    failures.Add($"Subagent[{i}]: {results[i].Error}");
                }
            }

            if (failures.Count > 0 && successResults.Count == 0)
            {
                LogParallelSubagentsAllFailed(_logger, failures.Count);
                return Result.Failure<IReadOnlyList<SubagentResult>>(
                    $"All {failures.Count} subagents failed: {string.Join("; ", failures)}");
            }

            if (failures.Count > 0)
            {
                LogParallelSubagentsPartialFailure(_logger, failures.Count, prompts.Count);
            }

            LogParallelSubagentsCompleted(_logger, successResults.Count, prompts.Count,
                stopwatch.Elapsed.TotalMilliseconds);

            return Result.Success<IReadOnlyList<SubagentResult>>(successResults);
        }
        finally
        {
            semaphore.Dispose();
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

    /// <summary>
    ///     Executes a subagent invocation with semaphore-based throttling.
    /// </summary>
    private async Task<Result<SubagentResult>> ExecuteWithSemaphore(
        SemaphoreSlim semaphore,
        string prompt,
        SubagentOptions options,
        CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);
        try
        {
            return await InvokeSubagentAsync(prompt, options, ct);
        }
        finally
        {
            semaphore.Release();
        }
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

    [LoggerMessage(EventId = 410, Level = LogLevel.Information,
        Message = "Subagent '{Label}' starting: PromptLength={PromptLength}")]
    private static partial void LogSubagentStarting(ILogger logger, string label, int promptLength);

    [LoggerMessage(EventId = 411, Level = LogLevel.Information,
        Message =
            "Subagent '{Label}' completed: Duration={DurationMs}ms, InputTokens={InputTokens}, OutputTokens={OutputTokens}")]
    private static partial void LogSubagentCompleted(ILogger logger, string label, double durationMs, int inputTokens,
        int outputTokens);

    [LoggerMessage(EventId = 412, Level = LogLevel.Warning,
        Message = "Subagent '{Label}' returned empty response")]
    private static partial void LogSubagentEmptyResponse(ILogger logger, string label);

    [LoggerMessage(EventId = 413, Level = LogLevel.Warning, Message = "Subagent '{Label}' was cancelled")]
    private static partial void LogSubagentCancelled(ILogger logger, string label);

    [LoggerMessage(EventId = 414, Level = LogLevel.Error,
        Message = "Subagent '{Label}' failed: {ExceptionMessage}")]
    private static partial void LogSubagentError(ILogger logger, Exception exception, string label,
        string exceptionMessage);

    [LoggerMessage(EventId = 420, Level = LogLevel.Information,
        Message = "Starting {Count} parallel subagents with max parallelism {MaxParallelism}")]
    private static partial void LogParallelSubagentsStarting(ILogger logger, int count, int maxParallelism);

    [LoggerMessage(EventId = 421, Level = LogLevel.Information,
        Message =
            "Parallel subagents completed: {SuccessCount}/{TotalCount} succeeded, Duration={DurationMs}ms")]
    private static partial void LogParallelSubagentsCompleted(ILogger logger, int successCount, int totalCount,
        double durationMs);

    [LoggerMessage(EventId = 422, Level = LogLevel.Error,
        Message = "All {FailureCount} parallel subagents failed")]
    private static partial void LogParallelSubagentsAllFailed(ILogger logger, int failureCount);

    [LoggerMessage(EventId = 423, Level = LogLevel.Warning,
        Message = "{FailureCount}/{TotalCount} parallel subagents failed")]
    private static partial void LogParallelSubagentsPartialFailure(ILogger logger, int failureCount, int totalCount);
}
