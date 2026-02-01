using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Daedalus.Application.Services.Middleware;

/// <summary>
///     Commits successful iterations to git as checkpoints.
///     Order: 500 (after loopback evaluation confirms build/tests pass).
/// </summary>
public sealed partial class GitCheckpointMiddleware(
    IGitWorkflowService gitService,
    ILogger<GitCheckpointMiddleware> logger) : IRalphLoopMiddleware
{
    public int Order => 500;

    public async Task<Result> InvokeAsync(
        RalphIterationContext context,
        Func<Task<Result>> continuation,
        CancellationToken ct)
    {
        try
        {
            if (!context.Configuration.EnableGitCheckpoints ||
                string.IsNullOrEmpty(context.Configuration.WorkspacePath))
            {
                return await continuation();
            }

            // Only checkpoint when loopback says build+tests pass
            if (context.LoopbackResult is { BuildSucceeded: true, TestsPassed: true })
            {
                var commitResult = await gitService.CommitAfterSuccessAsync(
                    context.Configuration.WorkspacePath,
                    context.Task.Id.ToString(),
                    context.Iteration,
                    $"Iteration {context.Iteration} passed verification",
                    ct);

                if (commitResult.IsFailure)
                {
                    LogGitCheckpointFailed(logger, context.Iteration, commitResult.Error);
                }
                else
                {
                    LogGitCheckpointCreated(logger, context.Iteration);
                }
            }

            return await continuation();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error during git checkpoint at iteration {Iteration}",
                context.Iteration);
            return await continuation();
        }
    }

    [LoggerMessage(EventId = 100, Level = LogLevel.Warning,
        Message = "Git checkpoint failed at iteration {Iteration}: {Error}")]
    private static partial void LogGitCheckpointFailed(ILogger logger, int iteration, string error);

    [LoggerMessage(EventId = 101, Level = LogLevel.Information,
        Message = "Git checkpoint created at iteration {Iteration}")]
    private static partial void LogGitCheckpointCreated(ILogger logger, int iteration);
}
