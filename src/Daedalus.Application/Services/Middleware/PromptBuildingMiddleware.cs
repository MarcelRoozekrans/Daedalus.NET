using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Daedalus.Application.Services.Middleware;

/// <summary>
///     Builds the prompt for this iteration using the structured RLP template.
///     Order: 100 (runs first — everything else needs the prompt).
/// </summary>
public sealed partial class PromptBuildingMiddleware(
    IPromptBuilder promptBuilder,
    ILogger<PromptBuildingMiddleware> logger) : IRalphLoopMiddleware
{
    public int Order => 100;

    public async Task<Result> InvokeAsync(
        RalphIterationContext context,
        Func<Task<Result>> continuation,
        CancellationToken ct)
    {
        try
        {
            var promptResult = await promptBuilder.BuildIterationPromptAsync(context.PromptContext, ct);

            if (promptResult.IsFailure)
            {
                logger.LogError("Failed to build iteration prompt: {Error}", promptResult.Error);
                return promptResult;
            }

            context.IterationPrompt = promptResult.Value;
            LogPromptBuilt(logger, context.Iteration, promptResult.Value.Length);

            return await continuation();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error during prompt building");
            return Result.Failure($"Prompt building failed: {ex.Message}");
        }
    }

    [LoggerMessage(EventId = 100, Level = LogLevel.Debug,
        Message = "Prompt built for iteration {Iteration}, length: {Length}")]
    private static partial void LogPromptBuilt(ILogger logger, int iteration, int length);
}
