using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Services.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace Daedalus.Application.Services.Middleware;

/// <summary>
///     Applies code changes extracted from the LLM response to the workspace filesystem.
///     Order: 250 (after LLM invocation at 200, before completion detection at 300).
///     Non-fatal on failure — continues the pipeline so loopback can catch build errors.
/// </summary>
public sealed partial class CodeChangeApplicationMiddleware(
    IGitChangeApplier changeApplier,
    ILogger<CodeChangeApplicationMiddleware> logger) : IRalphLoopMiddleware
{
    public int Order => 250;

    public async Task<Result> InvokeAsync(
        RalphIterationContext context,
        Func<Task<Result>> continuation,
        CancellationToken ct)
    {
        // Skip if LLM invocation failed or no workspace configured
        if (!context.LlmInvocationSucceeded ||
            string.IsNullOrEmpty(context.Configuration.WorkspacePath))
        {
            return await continuation();
        }

        var llmResponse = context.LlmResponse;
        if (string.IsNullOrEmpty(llmResponse))
        {
            return await continuation();
        }

        try
        {
            // Extract code blocks with file paths from LLM response
            var extractResult = await changeApplier.ExtractChangesAsync(llmResponse, ct);
            if (extractResult.IsFailure)
            {
                LogExtractionFailed(logger, context.Iteration, extractResult.Error);
                return await continuation(); // Non-fatal
            }

            var changes = extractResult.Value;
            if (changes.Count == 0)
            {
                LogNoChangesExtracted(logger, context.Iteration);
                return await continuation();
            }

            // Apply changes to workspace
            var applyResult = await changeApplier.ApplyChangesAsync(
                context.Configuration.WorkspacePath, changes, ct);

            if (applyResult.IsFailure)
            {
                LogApplyFailed(logger, context.Iteration, applyResult.Error);
                return await continuation(); // Non-fatal
            }

            // Store applied file paths for downstream middleware
            var appliedPaths = changes
                .Select(c => c.FilePath)
                .Where(p => !string.IsNullOrEmpty(p))
                .ToList();

            context.Items["applied_file_paths"] = appliedPaths;

            LogChangesApplied(logger, context.Iteration, changes.Count, context.Configuration.WorkspacePath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error applying code changes at iteration {Iteration}",
                context.Iteration);
            // Non-fatal — continue pipeline
        }

        return await continuation();
    }

    [LoggerMessage(EventId = 250, Level = LogLevel.Warning,
        Message = "Failed to extract code changes at iteration {Iteration}: {Error}")]
    private static partial void LogExtractionFailed(ILogger logger, int iteration, string error);

    [LoggerMessage(EventId = 251, Level = LogLevel.Debug,
        Message = "No code changes extracted from LLM response at iteration {Iteration}")]
    private static partial void LogNoChangesExtracted(ILogger logger, int iteration);

    [LoggerMessage(EventId = 252, Level = LogLevel.Warning,
        Message = "Failed to apply code changes at iteration {Iteration}: {Error}")]
    private static partial void LogApplyFailed(ILogger logger, int iteration, string error);

    [LoggerMessage(EventId = 253, Level = LogLevel.Information,
        Message = "Applied {ChangeCount} code changes at iteration {Iteration} to {WorkspacePath}")]
    private static partial void LogChangesApplied(ILogger logger, int iteration, int changeCount,
        string workspacePath);
}
