using System.Diagnostics;
using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Daedalus.Application.Services.Middleware;

/// <summary>
///     Invokes the LLM (with or without MCP) and captures the response.
///     Order: 200.
/// </summary>
public sealed partial class LlmInvocationMiddleware(
    ILlmService llmService,
    McpIntegrationOptions mcpOptions,
    ILogger<LlmInvocationMiddleware> logger) : IRalphLoopMiddleware
{
    public int Order => 200;

    public async Task<Result> InvokeAsync(
        RalphIterationContext context,
        Func<Task<Result>> continuation,
        CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrEmpty(context.IterationPrompt))
            {
                return Result.Failure("Iteration prompt not set");
            }

            var sw = Stopwatch.StartNew();

            var invokeResult = mcpOptions.Enabled && mcpOptions.Servers.Count > 0
                ? await llmService.InvokeWithMcpAsync(context.IterationPrompt, mcpOptions, ct)
                : await llmService.InvokeAsync(context.IterationPrompt, ct);

            sw.Stop();
            context.InvocationDuration = sw.Elapsed;

            if (invokeResult.IsFailure)
            {
                context.LlmInvocationSucceeded = false;
                context.ConsecutiveFailures++;
                LogLlmInvocationFailed(logger, context.Iteration, invokeResult.Error, context.ConsecutiveFailures);
                return await continuation();
            }

            context.LlmResponse = invokeResult.Value;
            context.LlmInvocationSucceeded = true;
            context.ConsecutiveFailures = 0;
            LogLlmInvocationSucceeded(logger, context.Iteration, context.InvocationDuration.TotalMilliseconds);

            return await continuation();
        }
        catch (Exception ex)
        {
            context.LlmInvocationSucceeded = false;
            context.ConsecutiveFailures++;
            logger.LogError(ex, "Unexpected error during LLM invocation at iteration {Iteration}",
                context.Iteration);
            return await continuation();
        }
    }

    [LoggerMessage(EventId = 100, Level = LogLevel.Warning,
        Message = "LLM invocation failed at iteration {Iteration}: {Error}, consecutive failures: {Count}")]
    private static partial void LogLlmInvocationFailed(ILogger logger, int iteration, string error, int count);

    [LoggerMessage(EventId = 101, Level = LogLevel.Debug,
        Message = "LLM invocation succeeded at iteration {Iteration}, duration: {Duration}ms")]
    private static partial void LogLlmInvocationSucceeded(ILogger logger, int iteration, double duration);
}
