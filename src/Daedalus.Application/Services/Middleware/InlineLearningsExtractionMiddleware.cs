using System.Globalization;
using System.Text;
using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Daedalus.Application.Services.Middleware;

/// <summary>
///     Parses each LLM response inline to extract structured learnings and feeds them
///     directly into the PromptContext for the next iteration. Runs every iteration —
///     no file I/O, no subagent calls, no every-3rd-iteration gating.
///     Replaces the file-based intermediary pattern with direct in-memory parsing.
///     Order: 350 (after CompletionDetection at 300, before LoopbackEvaluation at 400).
/// </summary>
public sealed partial class InlineLearningsExtractionMiddleware(
    ILlmResponseParser responseParser,
    ILogger<InlineLearningsExtractionMiddleware> logger) : IRalphLoopMiddleware
{
    public int Order => 350;

    public async Task<Result> InvokeAsync(
        RalphIterationContext context,
        Func<Task<Result>> continuation,
        CancellationToken ct)
    {
        try
        {
            // Only parse when we have a response to work with
            if (!context.LlmInvocationSucceeded || string.IsNullOrEmpty(context.LlmResponse))
            {
                return await continuation();
            }

            // Parse the response inline — zero I/O, pure pattern matching
            var parseResult = responseParser.ParseResponse(
                context.LlmResponse,
                context.Iteration,
                context.Task.CompletionPromise,
                context.PromptContext.AccumulatedLearnings);

            if (parseResult.IsFailure)
            {
                LogParsingFailed(logger, context.Iteration, parseResult.Error);
                return await continuation();
            }

            var parsed = parseResult.Value;
            context.ParsedLearnings = parsed;

            // Feed learnings directly into PromptContext for next iteration
            if (parsed.HasLearnings)
            {
                var learningText = FormatLearningsForPrompt(parsed, context.Iteration);
                var existing = context.PromptContext.AccumulatedLearnings ?? string.Empty;
                context.PromptContext.AccumulatedLearnings = string.IsNullOrEmpty(existing)
                    ? learningText
                    : $"{existing}\n{learningText}";

                LogLearningsInjected(logger, context.Iteration,
                    parsed.ErrorPatterns.Count, parsed.ApproachSignals.Count, parsed.ModifiedAreas.Count);
            }

            // Detect stuck patterns and flag for plan regeneration
            if (parsed.StuckDetected)
            {
                LogStuckDetected(logger, context.Iteration, context.Task.Id);
                context.PromptContext.Metadata["stuck_detected"] = "true";
                context.PromptContext.Metadata["stuck_at_iteration"] =
                    context.Iteration.ToString(CultureInfo.InvariantCulture);
            }

            // Also update Task.Learnings directly so it's persisted to DB by the pipeline
            if (parsed.HasLearnings)
            {
                var currentTaskLearnings = context.Task.Learnings ?? string.Empty;
                var newLearning = $"\n{parsed.CompactSummary}";
                context.Task.UpdateLearnings(currentTaskLearnings + newLearning);
            }

            return await continuation();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Unexpected error during inline learnings extraction at iteration {Iteration}",
                context.Iteration);
            // Non-fatal — continue pipeline
            return await continuation();
        }
    }

    /// <summary>
    ///     Formats parsed learnings into a compact text block for prompt injection.
    ///     Designed to be short and actionable — the LLM needs signals, not essays.
    /// </summary>
    private static string FormatLearningsForPrompt(ParsedIterationLearnings parsed, int iteration)
    {
        var sb = new StringBuilder(256);
        sb.Append(CultureInfo.InvariantCulture, $"[Iteration {iteration} Learnings]");
        sb.AppendLine();

        if (parsed.ErrorPatterns.Count > 0)
        {
            sb.AppendLine("  Errors detected:");
            foreach (var error in parsed.ErrorPatterns.Take(3))
            {
                sb.Append("    - ");
                sb.AppendLine(error.Length > 150 ? error[..150] + "..." : error);
            }
        }

        if (parsed.ApproachSignals.Count > 0)
        {
            sb.AppendLine("  Approach taken:");
            foreach (var approach in parsed.ApproachSignals.Take(2))
            {
                sb.Append("    - ");
                sb.AppendLine(approach.Length > 150 ? approach[..150] + "..." : approach);
            }
        }

        if (parsed.ModifiedAreas.Count > 0)
        {
            sb.Append("  Files touched: ");
            sb.AppendJoin(", ", parsed.ModifiedAreas.Take(5));
            sb.AppendLine();
        }

        if (parsed.StuckDetected)
        {
            sb.AppendLine("  ⚠ STUCK: Response repeats previous patterns — try a completely different approach.");
        }

        sb.Append(CultureInfo.InvariantCulture, $"  Progress: {parsed.ProgressSignal:P0}");
        sb.AppendLine();

        return sb.ToString();
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
        Message =
            "Inline learnings injected for iteration {Iteration}: errors={Errors}, approaches={Approaches}, files={Files}")]
    private static partial void LogLearningsInjected(ILogger logger, int iteration, int errors, int approaches,
        int files);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "Inline response parsing failed at iteration {Iteration}: {Error}")]
    private static partial void LogParsingFailed(ILogger logger, int iteration, string error);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
        Message = "Stuck pattern detected at iteration {Iteration} for task {TaskId}")]
    private static partial void LogStuckDetected(ILogger logger, int iteration, Guid taskId);
}
