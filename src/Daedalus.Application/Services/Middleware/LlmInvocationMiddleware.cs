using System.Diagnostics;
using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Daedalus.Application.Services.Middleware;

/// <summary>
///     Invokes the LLM agent and captures the response.
///     MCP tools are pre-attached by <see cref="IRalphAgentFactory" />,
///     so no MCP branching is needed at the call site.
///     Order: 200.
/// </summary>
public sealed partial class LlmInvocationMiddleware(
    IRalphAgentFactory agentFactory,
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

            // Single invocation path — MCP tools are pre-attached by the factory
            var invokeResult = await agentFactory.InvokeAsync(context.IterationPrompt, ct);

            sw.Stop();
            context.InvocationDuration = sw.Elapsed;

            if (invokeResult.IsFailure)
            {
                context.LlmInvocationSucceeded = false;
                context.ConsecutiveFailures++;
                LogLlmInvocationFailed(logger, context.Iteration, invokeResult.Error, context.ConsecutiveFailures);
                return await continuation();
            }

            var result = invokeResult.Value;
            context.LlmResponse = result.Response;
            context.InputTokens = result.InputTokens;
            context.OutputTokens = result.OutputTokens;
            context.ModelId = result.ModelId;
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
