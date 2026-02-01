using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Daedalus.Application.Services.Middleware;

/// <summary>
///     Runs build/test verification after each iteration.
///     Only activates when configured and when loopback evaluator is available.
///     Order: 400.
/// </summary>
public sealed partial class LoopbackEvaluationMiddleware(
    ILoopbackEvaluator evaluator,
    ILogger<LoopbackEvaluationMiddleware> logger) : IRalphLoopMiddleware
{
    public int Order => 400;

    public async Task<Result> InvokeAsync(
        RalphIterationContext context,
        Func<Task<Result>> continuation,
        CancellationToken ct)
    {
        try
        {
            if (!context.Configuration.EnableLoopbackEvaluation ||
                !context.LlmInvocationSucceeded ||
                string.IsNullOrEmpty(context.Configuration.WorkspacePath))
            {
                return await continuation();
            }

            var evalResult = await evaluator.EvaluateAsync(
                context.Configuration.WorkspacePath,
                context.LlmResponse ?? string.Empty,
                ct);

            if (evalResult.IsSuccess)
            {
                context.LoopbackResult = evalResult.Value;
                LogLoopbackEvaluationCompleted(logger, context.Iteration, evalResult.Value.BuildSucceeded,
                    evalResult.Value.TestsPassed);
            }
            else
            {
                LogLoopbackEvaluationFailed(logger, evalResult.Error);
            }

            return await continuation();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error during loopback evaluation at iteration {Iteration}",
                context.Iteration);
            return await continuation();
        }
    }

    [LoggerMessage(EventId = 100, Level = LogLevel.Debug,
        Message = "Loopback evaluation completed at iteration {Iteration}: Build={Build}, Tests={Tests}")]
    private static partial void LogLoopbackEvaluationCompleted(ILogger logger, int iteration, bool build, bool tests);

    [LoggerMessage(EventId = 101, Level = LogLevel.Warning, Message = "Loopback evaluation failed: {Error}")]
    private static partial void LogLoopbackEvaluationFailed(ILogger logger, string error);
}
