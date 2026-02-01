using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Anthropic;
using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Services;
using Microsoft.Extensions.Logging;

namespace Daedalus.Infrastructure.Llm;

/// <summary>
///     LLM service implementation using the Anthropic Claude API directly.
///     Supports subagent invocations for the Ralph Wiggum technique:
///     the primary context window acts as a scheduler, spawning subagents
///     for expensive operations to preserve context budget.
/// </summary>
[SuppressMessage("Design", "CA1031:Do not catch general exception types",
    Justification = "LLM invocations should return Result failures, not throw")]
public sealed partial class ClaudeLlmService(
    AnthropicApi client,
    string defaultModel,
    int maxTokens,
    ILogger<ClaudeLlmService> logger) : ILlmService, IDisposable
{
    private static readonly Uri AnthropicApiBaseUri = new("https://api.anthropic.com/v1/");

    private bool _disposed;

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            client.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public string ProviderName => "claude";

    /// <inheritdoc />
    public bool SupportsSubagents => true;

    /// <inheritdoc />
    public async Task<Result<string>> InvokeAsync(string prompt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return Result.Failure<string>("Prompt cannot be empty");
        }

        try
        {
            LogInvocationStarting(logger, prompt.Length, defaultModel);

            var request = new CreateMessageRequest
            {
                MaxTokens = maxTokens,
                Messages = [new Message { Content = prompt, Role = MessageRole.User }],
                Model = defaultModel
            };

            var response = await client.CreateMessageAsync(request, ct).ConfigureAwait(false);

            var content = ExtractTextContent(response);

            if (string.IsNullOrEmpty(content))
            {
                LogEmptyResponse(logger);
                return Result.Failure<string>("Claude returned empty response");
            }

            LogInvocationCompleted(logger, content.Length,
                response.Usage?.InputTokens ?? 0, response.Usage?.OutputTokens ?? 0);
            return Result.Success(content);
        }
        catch (OperationCanceledException)
        {
            LogInvocationCancelled(logger);
            return Result.Failure<string>("Operation cancelled");
        }
        catch (Exception ex)
        {
            LogErrorInvoking(logger, ex, ex.Message);
            return Result.Failure<string>($"Error invoking Claude: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<Result<string>> InvokeWithMcpAsync(
        string prompt,
        McpIntegrationOptions mcpOptions,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return Result.Failure<string>("Prompt cannot be empty");
        }

        if (mcpOptions is not { Enabled: true, Servers.Count: > 0 })
        {
            return await InvokeAsync(prompt, ct).ConfigureAwait(false);
        }

        try
        {
            LogMcpInvocationStarting(logger, prompt.Length, mcpOptions.Servers.Count);

            // Build a system prompt that describes available MCP servers
            var systemPrompt = BuildMcpSystemPrompt(mcpOptions);

            var request = new CreateMessageRequest
            {
                MaxTokens = maxTokens,
                System = systemPrompt,
                Messages = [new Message { Content = prompt, Role = MessageRole.User }],
                Model = defaultModel
            };

            var response = await client.CreateMessageAsync(request, ct).ConfigureAwait(false);

            var content = ExtractTextContent(response);

            if (string.IsNullOrEmpty(content))
            {
                return Result.Failure<string>("Claude returned empty response with MCP context");
            }

            LogMcpInvocationCompleted(logger, content.Length);
            return Result.Success(content);
        }
        catch (OperationCanceledException)
        {
            LogInvocationCancelled(logger);
            return Result.Failure<string>("Operation cancelled");
        }
        catch (Exception ex)
        {
            LogErrorInvokingWithMcp(logger, ex, ex.Message);
            return Result.Failure<string>($"Error invoking Claude with MCP: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<Result<SubagentResult>> InvokeSubagentAsync(
        string prompt,
        SubagentOptions options,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return Result.Failure<SubagentResult>("Subagent prompt cannot be empty");
        }

        var label = options.Label ?? "unnamed";

        try
        {
            LogSubagentStarting(logger, label, prompt.Length);
            var stopwatch = Stopwatch.StartNew();

            var modelToUse = options.ModelOverride ?? defaultModel;

            var request = new CreateMessageRequest
            {
                MaxTokens = options.MaxTokens,
                Messages = [new Message { Content = prompt, Role = MessageRole.User }],
                Model = modelToUse
            };

            // Add system prompt if provided
            if (!string.IsNullOrWhiteSpace(options.SystemPrompt))
            {
                request.System = options.SystemPrompt;
            }

            // Use subagent-specific timeout via CancellationToken
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(options.Timeout);

            var response = await client.CreateMessageAsync(request, timeoutCts.Token).ConfigureAwait(false);

            stopwatch.Stop();

            var textContent = ExtractTextContent(response);

            if (string.IsNullOrEmpty(textContent))
            {
                LogSubagentEmptyResponse(logger, label);
                return Result.Failure<SubagentResult>($"Subagent '{label}' returned empty response");
            }

            var inputTokens = response.Usage?.InputTokens ?? 0;
            var outputTokens = response.Usage?.OutputTokens ?? 0;

            var result = new SubagentResult
            {
                Response = textContent,
                Label = label,
                Duration = stopwatch.Elapsed,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                UsedExtendedThinking = false,
                ThinkingContent = null
            };

            LogSubagentCompleted(logger, label, stopwatch.Elapsed.TotalMilliseconds,
                inputTokens, outputTokens);

            return Result.Success(result);
        }
        catch (OperationCanceledException)
        {
            LogSubagentCancelled(logger, label);
            return Result.Failure<SubagentResult>($"Subagent '{label}' was cancelled");
        }
        catch (Exception ex)
        {
            LogSubagentError(logger, ex, label, ex.Message);
            return Result.Failure<SubagentResult>($"Subagent '{label}' failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<SubagentResult>>> InvokeParallelSubagentsAsync(
        IReadOnlyList<string> prompts,
        SubagentOptions options,
        int maxParallelism = 10,
        CancellationToken ct = default)
    {
        if (prompts.Count == 0)
        {
            return Result.Success<IReadOnlyList<SubagentResult>>(Array.Empty<SubagentResult>());
        }

        LogParallelSubagentsStarting(logger, prompts.Count, maxParallelism);
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
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            stopwatch.Stop();

            // Collect results, report any failures
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
                LogParallelSubagentsAllFailed(logger, failures.Count);
                return Result.Failure<IReadOnlyList<SubagentResult>>(
                    $"All {failures.Count} subagents failed: {string.Join("; ", failures)}");
            }

            if (failures.Count > 0)
            {
                LogParallelSubagentsPartialFailure(logger, failures.Count, prompts.Count);
            }

            LogParallelSubagentsCompleted(logger, successResults.Count, prompts.Count,
                stopwatch.Elapsed.TotalMilliseconds);

            return Result.Success<IReadOnlyList<SubagentResult>>(successResults);
        }
        finally
        {
            semaphore.Dispose();
        }
    }

    /// <summary>
    ///     Creates a Claude LLM service with API key authentication.
    /// </summary>
    public static ClaudeLlmService CreateWithApiKey(
        string apiKey,
        string model,
        int maxTokens,
        TimeSpan timeout,
        ILogger<ClaudeLlmService> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        var httpClient = new HttpClient { Timeout = timeout };
        var client = new AnthropicApi(httpClient, AnthropicApiBaseUri);
        client.AuthorizeUsingApiKey(apiKey);

        return new ClaudeLlmService(client, model, maxTokens, logger);
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
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await InvokeSubagentAsync(prompt, options, ct).ConfigureAwait(false);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    ///     Extracts text content from a Claude API response.
    ///     Response Content is OneOf&lt;string, IList&lt;Block&gt;&gt;.
    /// </summary>
    private static string ExtractTextContent(Message response)
    {
        // Content can be a plain string or a list of blocks
        if (response.Content.IsValue1)
        {
            return response.Content.Value1?.Trim() ?? string.Empty;
        }

        if (!response.Content.IsValue2)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();

        foreach (var block in response.Content.Value2)
        {
            if (block.IsText)
            {
                builder.Append(block.Text.Text);
            }
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    ///     Builds a system prompt describing available MCP servers for context enrichment.
    /// </summary>
    private static string BuildMcpSystemPrompt(McpIntegrationOptions mcpOptions)
    {
        var builder = new StringBuilder();
        builder.AppendLine("You have access to the following external tools via Model Context Protocol (MCP):");
        builder.AppendLine();

        foreach (var (serverName, serverConfig) in mcpOptions.Servers)
        {
            if (serverConfig is { Url: not null })
            {
                builder.AppendLine(CultureInfo.InvariantCulture,
                    $"- **{serverName}**: {serverConfig.Type} server at {serverConfig.Url}");

                if (serverConfig.Tools is { Count: > 0 })
                {
                    var toolsList = string.Join(", ", serverConfig.Tools);
                    builder.AppendLine(CultureInfo.InvariantCulture, $"  Tools: {toolsList}");
                }
            }
        }

        builder.AppendLine();
        builder.AppendLine(
            "When referencing these tools, describe what you need and the relevant MCP server will be queried.");

        return builder.ToString();
    }

    // ============== Logging Methods ==============

    [LoggerMessage(EventId = 100, Level = LogLevel.Information,
        Message = "Claude invocation starting: PromptLength={PromptLength}, Model={Model}")]
    private static partial void LogInvocationStarting(ILogger logger, int promptLength, string model);

    [LoggerMessage(EventId = 101, Level = LogLevel.Information,
        Message =
            "Claude invocation completed: ResponseLength={ResponseLength}, InputTokens={InputTokens}, OutputTokens={OutputTokens}")]
    private static partial void LogInvocationCompleted(ILogger logger, int responseLength, int inputTokens,
        int outputTokens);

    [LoggerMessage(EventId = 102, Level = LogLevel.Warning, Message = "Claude returned empty response")]
    private static partial void LogEmptyResponse(ILogger logger);

    [LoggerMessage(EventId = 103, Level = LogLevel.Information, Message = "Claude invocation cancelled")]
    private static partial void LogInvocationCancelled(ILogger logger);

    [LoggerMessage(EventId = 104, Level = LogLevel.Error, Message = "Error invoking Claude: {ExceptionMessage}")]
    private static partial void LogErrorInvoking(ILogger logger, Exception exception, string exceptionMessage);

    [LoggerMessage(EventId = 105, Level = LogLevel.Information,
        Message = "Claude MCP invocation starting: PromptLength={PromptLength}, ServerCount={ServerCount}")]
    private static partial void LogMcpInvocationStarting(ILogger logger, int promptLength, int serverCount);

    [LoggerMessage(EventId = 106, Level = LogLevel.Information,
        Message = "Claude MCP invocation completed: ResponseLength={ResponseLength}")]
    private static partial void LogMcpInvocationCompleted(ILogger logger, int responseLength);

    [LoggerMessage(EventId = 107, Level = LogLevel.Error,
        Message = "Error invoking Claude with MCP: {ExceptionMessage}")]
    private static partial void LogErrorInvokingWithMcp(ILogger logger, Exception exception, string exceptionMessage);

    [LoggerMessage(EventId = 110, Level = LogLevel.Information,
        Message = "Subagent '{Label}' starting: PromptLength={PromptLength}")]
    private static partial void LogSubagentStarting(ILogger logger, string label, int promptLength);

    [LoggerMessage(EventId = 111, Level = LogLevel.Information,
        Message =
            "Subagent '{Label}' completed: Duration={DurationMs}ms, InputTokens={InputTokens}, OutputTokens={OutputTokens}")]
    private static partial void LogSubagentCompleted(ILogger logger, string label, double durationMs, int inputTokens,
        int outputTokens);

    [LoggerMessage(EventId = 112, Level = LogLevel.Warning,
        Message = "Subagent '{Label}' returned empty response")]
    private static partial void LogSubagentEmptyResponse(ILogger logger, string label);

    [LoggerMessage(EventId = 113, Level = LogLevel.Warning, Message = "Subagent '{Label}' was cancelled")]
    private static partial void LogSubagentCancelled(ILogger logger, string label);

    [LoggerMessage(EventId = 114, Level = LogLevel.Error,
        Message = "Subagent '{Label}' failed: {ExceptionMessage}")]
    private static partial void LogSubagentError(ILogger logger, Exception exception, string label,
        string exceptionMessage);

    [LoggerMessage(EventId = 120, Level = LogLevel.Information,
        Message = "Starting {Count} parallel subagents with max parallelism {MaxParallelism}")]
    private static partial void LogParallelSubagentsStarting(ILogger logger, int count, int maxParallelism);

    [LoggerMessage(EventId = 121, Level = LogLevel.Information,
        Message =
            "Parallel subagents completed: {SuccessCount}/{TotalCount} succeeded, Duration={DurationMs}ms")]
    private static partial void LogParallelSubagentsCompleted(ILogger logger, int successCount, int totalCount,
        double durationMs);

    [LoggerMessage(EventId = 122, Level = LogLevel.Error,
        Message = "All {FailureCount} parallel subagents failed")]
    private static partial void LogParallelSubagentsAllFailed(ILogger logger, int failureCount);

    [LoggerMessage(EventId = 123, Level = LogLevel.Warning,
        Message = "{FailureCount}/{TotalCount} parallel subagents failed")]
    private static partial void LogParallelSubagentsPartialFailure(ILogger logger, int failureCount,
        int totalCount);
}
