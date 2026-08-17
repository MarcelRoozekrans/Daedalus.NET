using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Daedalus.Application.Services.Middleware;

/// <summary>
///     Enriches the prompt context with cross-task learnings and failure patterns
///     before prompt building occurs.
///     Order: 90 (runs before PromptBuildingMiddleware at 100).
///     Operates in two modes based on <see cref="IKnowledgeBaseToolStatus.AreToolsAvailable" />:
///     <list type="bullet">
///         <item>
///             <term>Slim mode</term>
///             <description>
///                 When MCP knowledge base tools are available, injects a compact summary
///                 and tool-usage hint so the LLM can query the knowledge base on demand.
///             </description>
///         </item>
///         <item>
///             <term>Full-text fallback</term>
///             <description>
///                 When MCP tools are unavailable, fetches learnings and failure patterns
///                 from the database and injects them directly into the prompt context.
///             </description>
///         </item>
///     </list>
/// </summary>
public sealed partial class LearningsEnrichmentMiddleware(
    ILearningsService learningsService,
    IKnowledgeBaseToolStatus toolStatus,
    ILogger<LearningsEnrichmentMiddleware> logger) : IRalphLoopMiddleware
{
    /// <summary>
    ///     Maximum structured learnings entries to inject per iteration.
    /// </summary>
    private const int _maxLearnings = 10;

    /// <summary>
    ///     Maximum failure pattern records to inject per iteration.
    /// </summary>
    private const int _maxFailurePatterns = 5;

    public int Order => 90;

    public async Task<Result> InvokeAsync(
        RalphIterationContext context,
        Func<Task<Result>> continuation,
        CancellationToken ct)
    {
        try
        {
            // Only enrich on first iteration or every 3rd iteration to avoid noise
            if (context.Iteration != 1 && context.Iteration % 3 != 0)
            {
                return await continuation();
            }

            if (toolStatus.AreToolsAvailable)
            {
                // Slim mode: inject summary + tool usage hint
                var summary = $"=== KNOWLEDGE BASE ===\n" +
                    $"You have access to a knowledge base with {toolStatus.LearningsCount} learnings " +
                    $"and {toolStatus.FailurePatternsCount} failure patterns.\n" +
                    $"Use the search_learnings tool to find relevant past knowledge.\n" +
                    $"Use the search_failure_patterns tool when you encounter errors.\n";

                context.PromptContext.AccumulatedLearnings = summary;
                LogSlimEnrichment(logger, context.Iteration, toolStatus.LearningsCount);
            }
            else
            {
                // Fallback: full text injection (current behavior)
                var enrichmentResult = await learningsService.GetEnrichmentContextAsync(
                    context.Task.Prompt,
                    context.Task.ProjectId != Guid.Empty ? context.Task.ProjectId : null,
                    context.Task.Id,
                    _maxLearnings,
                    _maxFailurePatterns,
                    ct);

                if (enrichmentResult.IsSuccess && !string.IsNullOrEmpty(enrichmentResult.Value))
                {
                    // Inject enrichment into the prompt context's accumulated learnings
                    // This gets picked up by DefaultPromptBuilder in the "LEARNINGS" section
                    var existingLearnings = context.PromptContext.AccumulatedLearnings ?? string.Empty;
                    context.PromptContext.AccumulatedLearnings = string.IsNullOrEmpty(existingLearnings)
                        ? enrichmentResult.Value
                        : $"{existingLearnings}\n\n{enrichmentResult.Value}";

                    LogEnrichmentInjected(logger, context.Iteration, enrichmentResult.Value.Length);
                }
                else if (enrichmentResult.IsFailure)
                {
                    LogEnrichmentFailed(logger, context.Iteration, enrichmentResult.Error);
                    // Non-fatal — continue without enrichment
                }
                else
                {
                    LogNoEnrichment(logger, context.Iteration);
                }
            }

            return await continuation();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Unexpected error during learnings enrichment at iteration {Iteration}",
                context.Iteration);
            // Non-fatal — continue pipeline without enrichment
            return await continuation();
        }
    }

    [LoggerMessage(EventId = 100, Level = LogLevel.Debug,
        Message = "Learnings enrichment injected for iteration {Iteration}, enrichment length: {Length}")]
    private static partial void LogEnrichmentInjected(ILogger logger, int iteration, int length);

    [LoggerMessage(EventId = 101, Level = LogLevel.Debug,
        Message = "No enrichment context available for iteration {Iteration}")]
    private static partial void LogNoEnrichment(ILogger logger, int iteration);

    [LoggerMessage(EventId = 102, Level = LogLevel.Warning,
        Message = "Learnings enrichment failed for iteration {Iteration}: {Error}")]
    private static partial void LogEnrichmentFailed(ILogger logger, int iteration, string error);

    [LoggerMessage(EventId = 103, Level = LogLevel.Debug,
        Message = "Slim enrichment mode for iteration {Iteration}: {LearningsCount} learnings available via MCP tools")]
    private static partial void LogSlimEnrichment(ILogger logger, int iteration, int learningsCount);
}
