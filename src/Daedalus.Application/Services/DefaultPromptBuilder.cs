using System.Text;
using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Daedalus.Application.Services;

/// <summary>
///     Default implementation of prompt building for Ralph loop execution.
///     Implements the iterative prompt enhancement pattern from the Ralph Wiggum loop.
///     Integrates the RLP structured template system, workspace context loading,
///     and Context7 documentation injection for LLM grounding.
/// </summary>
/// <remarks>
///     The Ralph loop pattern (from the RLP technique):
///     1. First iteration: Build structured prompt from RLP template sections with workspace context
///     (specs, plan, AGENT.md), Context7 docs, and numbered priority instructions
///     2. Subsequent iterations:
///     - Include the same structured template (deterministic stack allocation)
///     - Add execution history and what was previously tried
///     - Ask the LLM to refine its approach
///     - Provide hints about what the completion promise is
///     - Include updated Context7 documentation references
///     - Track progress and failures
/// </remarks>
public sealed partial class DefaultPromptBuilder(
    IOptions<RalphLoopConfiguration> ralphConfig,
    IContext7DocumentationInjector documentationInjector,
    IRalphPromptTemplateBuilder templateBuilder,
    IWorkspaceContextProvider workspaceContextProvider,
    IPromptContextStore promptContextStore,
    ILogger<DefaultPromptBuilder> logger)
    : IPromptBuilder
{
    /// <summary>
    ///     Maximum history entries to include in the prompt.
    ///     Prevents prompt from becoming too large.
    /// </summary>
    private const int _maxHistoryInPrompt = 10;

    public Task<Result<PromptContext>> InitializeContextAsync(
        Guid taskId,
        Guid sessionId,
        string originalPrompt,
        string completionPromise,
        string? learnings,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(originalPrompt))
        {
            return Task.FromResult(Result.Failure<PromptContext>("Original prompt cannot be empty"));
        }

        if (string.IsNullOrWhiteSpace(completionPromise))
        {
            return Task.FromResult(Result.Failure<PromptContext>("Completion promise cannot be empty"));
        }

        var context = new PromptContext
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            SessionId = sessionId,
            OriginalPrompt = originalPrompt,
            CompletionPromise = completionPromise,
            AccumulatedLearnings = learnings,
            Iteration = 0,
            Metadata =
            {
                ["started_at"] = DateTime.UtcNow.ToString("O"),
                ["total_iterations"] = "0",
                ["consecutive_failures"] = "0"
            }
        };

        // Initialize template options for structured prompt assembly
        // Map configuration from RalphLoopConfiguration to prompt options
        var config = ralphConfig.Value;
        context.TemplateOptions = new RalphPromptTemplateOptions
        {
            TaskPrompt = originalPrompt,
            CompletionPromise = completionPromise,
            UseArticleStylePrompts = config.UseArticleStylePrompts,
            MaxItemsPerIteration = config.MaxItemsPerIteration,
            MaxParallelSubagents = config.MaxSearchSubagents,
            IncludeGitWorkflow = true,
            IncludeTestInstructions = true,
            IncludeQualityGuards = true,
            IncludeSelfImprovement = true,
            IncludeCodingStandards = true,
            IncludeLoggingInstructions = true
        };

        var hasLearnings = !string.IsNullOrEmpty(learnings);
        LogContextInitialized(logger, taskId, sessionId, completionPromise.Length, hasLearnings);

        return Task.FromResult(Result.Success(context));
    }

    public async Task<Result<string>> BuildIterationPromptAsync(
        PromptContext? context,
        CancellationToken ct)
    {
        if (context == null)
        {
            return Result.Failure<string>("Prompt context cannot be null");
        }

        context.Iteration++;

        // Get Context7 documentation for the prompt
        var docResult = await documentationInjector.GetDocumentationContextAsync(context.OriginalPrompt, ct);

        // Build the structured RLP template prompt (same structure every iteration)
        var templatePrompt = await BuildTemplatePromptAsync(context, ct);

        // First iteration: use the structured template prompt with Context7 docs
        if (context.Iteration == 1)
        {
            LogBuildingFirstIteration(logger, context.TaskId);

            var prompt = templatePrompt ?? context.OriginalPrompt;

            // Prepend Context7 documentation if available
            if (docResult.IsSuccess && !string.IsNullOrEmpty(docResult.Value))
            {
                prompt = $"{docResult.Value}\n\n{prompt}";
            }

            return Result.Success(prompt);
        }

        // Subsequent iterations: enhance the structured template with history context
        var enhancedPrompt = BuildEnhancedPrompt(
            context,
            docResult.IsSuccess ? docResult.Value : null,
            templatePrompt);

        var hasDocs = docResult.IsSuccess && !string.IsNullOrEmpty(docResult.Value);
        var hasTemplate = templatePrompt != null;
        LogBuiltEnhancedPrompt(logger, context.Iteration, context.TaskId, context.History.Count, enhancedPrompt.Length,
            hasDocs, hasTemplate);

        return Result.Success(enhancedPrompt);
    }

    public Task<Result> RecordIterationResultAsync(
        PromptContext? context,
        int iterationNumber,
        string prompt,
        string response,
        bool completionPromiseFound,
        TimeSpan duration,
        string? executionError,
        CancellationToken ct)
    {
        if (context == null)
        {
            return Task.FromResult(Result.Failure("Prompt context cannot be null"));
        }

        var entry = new PromptHistoryEntry
        {
            Iteration = iterationNumber,
            Prompt = prompt,
            Response = response,
            CompletionPromiseFound = completionPromiseFound,
            Duration = duration,
            ExecutionError = executionError,
            ExecutedAt = DateTime.UtcNow,
            CompactSummary = PromptHistoryEntry.BuildCompactSummary(
                response, executionError, completionPromiseFound, duration)
        };

        context.History.Add(entry);
        context.UpdatedAt = DateTime.UtcNow;

        // Update metadata
#pragma warning disable CA1305, MA0011 // Invariant culture not needed for metadata
        context.Metadata["total_iterations"] = iterationNumber.ToString();

        if (!string.IsNullOrEmpty(executionError))
        {
            if (context.Metadata.TryGetValue("consecutive_failures", out var failureStr) &&
                int.TryParse(failureStr, out var failures))
            {
                context.Metadata["consecutive_failures"] = (failures + 1).ToString();
            }
            else
            {
                context.Metadata["consecutive_failures"] = "1";
            }
        }
        else
        {
            context.Metadata["consecutive_failures"] = "0";
        }
#pragma warning restore CA1305, MA0011

        var hasError = !string.IsNullOrEmpty(executionError);
        LogIterationResultRecorded(logger, iterationNumber, context.TaskId, completionPromiseFound,
            hasError, duration.TotalMilliseconds);

        return Task.FromResult(Result.Success());
    }

    public async Task<Result> PersistContextAsync(
        PromptContext? context,
        CancellationToken ct)
    {
        if (context == null)
        {
            return Result.Failure("Prompt context cannot be null");
        }

        var saveResult = await promptContextStore.SaveAsync(context, ct);
        if (saveResult.IsFailure)
        {
            LogPersistFailed(logger, context.TaskId, saveResult.Error);
            // Non-fatal: log and continue
        }

        context.UpdatedAt = DateTime.UtcNow;

        return Result.Success();
    }

    public async Task<Result<PromptContext>> LoadContextAsync(
        Guid taskId,
        Guid sessionId,
        CancellationToken ct)
    {
        var loadResult = await promptContextStore.LoadAsync(taskId, sessionId, ct);
        if (loadResult.IsFailure)
        {
            LogNoSavedContext(logger, taskId, sessionId, loadResult.Error);
            return loadResult;
        }

        LogContextLoaded(logger, taskId, sessionId, loadResult.Value.Iteration);

        return loadResult;
    }

    /// <summary>
    ///     Builds a structured RLP template prompt using the template builder and workspace context.
    ///     Returns null if template building fails (falls back to original prompt).
    /// </summary>
    private async Task<string?> BuildTemplatePromptAsync(PromptContext context, CancellationToken ct)
    {
        if (context.TemplateOptions == null)
        {
            return null;
        }

        try
        {
            // Load workspace context if a workspace path is available and not already loaded
            if (context.WorkspaceContext == null &&
                context.Metadata.TryGetValue("workspace_path", out var workspacePath) &&
                !string.IsNullOrEmpty(workspacePath))
            {
                var wsResult = await workspaceContextProvider.LoadWorkspaceContextAsync(workspacePath, ct: ct);
                if (wsResult.IsSuccess)
                {
                    context.WorkspaceContext = wsResult.Value;
                    context.TemplateOptions.WorkspaceContext = wsResult.Value;
                }
            }

            // Build the structured prompt from RLP template sections
            var templateResult = await templateBuilder.BuildPromptAsync(context.TemplateOptions, ct);
            if (templateResult.IsSuccess)
            {
                return templateResult.Value;
            }

            LogTemplateBuildFailed(logger, context.TaskId, templateResult.Error);
        }
        catch (Exception ex)
        {
            LogTemplateBuildException(logger, ex, context.TaskId);
        }

        return null;
    }

    /// <summary>
    ///     Builds an enhanced prompt that includes the structured RLP template,
    ///     Context7 documentation, history context and execution hints.
    ///     This is the core Ralph loop pattern implementation with documentation grounding.
    ///     The key RLP insight: deterministically allocate the stack (specs, plan) the same way
    ///     every loop, then append iteration-specific context.
    /// </summary>
#pragma warning disable CA1305, CA1845, MA0011 // Locale-aware string formatting not needed for LLM prompts
    private static string BuildEnhancedPrompt(
        PromptContext context,
        string? context7Documentation = null,
        string? templatePrompt = null)
    {
        var sb = new StringBuilder();

        // Add Context7 documentation first for LLM grounding
        if (!string.IsNullOrEmpty(context7Documentation))
        {
            sb.AppendLine(context7Documentation);
            sb.AppendLine();
        }

        // Use the structured RLP template if available, otherwise fall back to original prompt
        // This is the "deterministic stack allocation" — same structure every iteration
        if (!string.IsNullOrEmpty(templatePrompt))
        {
            sb.AppendLine(templatePrompt);
        }
        else
        {
            sb.AppendLine(context.OriginalPrompt);
        }

        sb.AppendLine();

        // Add context section
        sb.AppendLine("=== RALPH LOOP CONTEXT ===");
        sb.AppendLine($"Iteration: {context.Iteration} of many possible iterations");
        sb.AppendLine($"Completion Goal: Find this exact string in your output: \"{context.CompletionPromise}\"");
        sb.AppendLine();

        // Add accumulated learnings if available
        if (!string.IsNullOrEmpty(context.AccumulatedLearnings))
        {
            sb.AppendLine("=== LEARNINGS FROM PREVIOUS EXECUTIONS ===");
            sb.AppendLine("Apply these learnings to avoid repeating known issues:");
            sb.AppendLine();
            sb.AppendLine(context.AccumulatedLearnings);
            sb.AppendLine();
        }

        // Add execution history if available
        if (context.History.Count > 0)
        {
            sb.AppendLine("=== PREVIOUS ATTEMPTS ===");

            var historyToInclude = context.History
                .TakeLast(_maxHistoryInPrompt)
                .ToList();

            for (var i = 0; i < historyToInclude.Count; i++)
            {
                var entry = historyToInclude[i];
                sb.AppendLine($"Attempt {entry.Iteration}:");

                // Use compact summary for context window efficiency
                // The article's key insight: "use as little of [the context window] as possible"
                if (!string.IsNullOrEmpty(entry.CompactSummary))
                {
                    sb.AppendLine($"  {entry.CompactSummary}");
                }
                else if (!string.IsNullOrEmpty(entry.ExecutionError))
                {
                    sb.AppendLine("  Status: ERROR");
                    sb.AppendLine($"  Error: {entry.ExecutionError}");
                }
                else if (entry.CompletionPromiseFound)
                {
                    sb.AppendLine("  Status: SUCCESS - Found completion promise!");
                }
                else
                {
                    sb.AppendLine("  Status: INCOMPLETE - Did not find completion promise");
                    sb.AppendLine($"  Response length: {entry.Response.Length} characters");
                }

                sb.AppendLine();
            }

            // Add progress hint
            sb.AppendLine("=== GUIDANCE FOR THIS ITERATION ===");
            var failureCount = context.History.Count(h => !string.IsNullOrEmpty(h.ExecutionError));
            var incompleteCount =
                context.History.Count(h => string.IsNullOrEmpty(h.ExecutionError) && !h.CompletionPromiseFound);

            if (failureCount > 0)
            {
                sb.AppendLine($"⚠ Previous {failureCount} attempts had errors.");
                sb.AppendLine("✓ Try a different approach or verify your parameters.");
            }

            if (incompleteCount > 0)
            {
                sb.AppendLine($"ℹ Previous {incompleteCount} attempts didn't find the completion promise.");
                sb.AppendLine($"✓ The string you need to output is: \"{context.CompletionPromise}\"");
                sb.AppendLine("✓ Make sure your response includes this exact phrase.");
            }

            sb.AppendLine(
                $"📊 Progress: {context.History.Count(h => !h.CompletionPromiseFound && string.IsNullOrEmpty(h.ExecutionError))} incomplete attempts, please continue refining your approach.");
            sb.AppendLine();
        }

        sb.AppendLine("=== YOUR TASK ===");
        sb.AppendLine("Continue working on the above task. Remember to include the completion phrase in your output.");
        sb.AppendLine("If you've found it, great! If not, try a different angle.");

        return sb.ToString();
    }
#pragma warning restore CA1305, CA1845, MA0011

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message =
            "Initialized prompt context for task {TaskId}, session {SessionId}, completion_promise_length={PromiseLength}, has_learnings={HasLearnings}")]
    private static partial void LogContextInitialized(ILogger logger, Guid taskId, Guid sessionId, int promiseLength,
        bool hasLearnings);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug,
        Message = "Building first iteration prompt for task {TaskId}, using RLP template with Context7 documentation")]
    private static partial void LogBuildingFirstIteration(ILogger logger, Guid taskId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug,
        Message =
            "Built enhanced iteration {Iteration} prompt for task {TaskId}, history_entries={HistoryCount}, prompt_length={PromptLength}, has_context7_docs={HasDocs}, has_template={HasTemplate}")]
    private static partial void LogBuiltEnhancedPrompt(ILogger logger, int iteration, Guid taskId, int historyCount,
        int promptLength, bool hasDocs, bool hasTemplate);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug,
        Message =
            "Recorded iteration {Iteration} result for task {TaskId}: completion_found={Found}, error={HasError}, duration_ms={DurationMs}")]
    private static partial void LogIterationResultRecorded(ILogger logger, int iteration, Guid taskId, bool found,
        bool hasError, double durationMs);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning,
        Message = "Failed to persist prompt context for task {TaskId}: {Error}")]
    private static partial void LogPersistFailed(ILogger logger, Guid taskId, string error);

    [LoggerMessage(EventId = 6, Level = LogLevel.Debug,
        Message = "No saved prompt context for task {TaskId}, session {SessionId}: {Error}")]
    private static partial void LogNoSavedContext(ILogger logger, Guid taskId, Guid sessionId, string error);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information,
        Message = "Loaded saved prompt context for task {TaskId}, session {SessionId}, iteration {Iteration}")]
    private static partial void LogContextLoaded(ILogger logger, Guid taskId, Guid sessionId, int iteration);

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning,
        Message = "RLP template building failed for task {TaskId}: {Error}. Falling back to original prompt")]
    private static partial void LogTemplateBuildFailed(ILogger logger, Guid taskId, string error);

    [LoggerMessage(EventId = 9, Level = LogLevel.Warning,
        Message = "Exception during RLP template building for task {TaskId}. Falling back to original prompt")]
    private static partial void LogTemplateBuildException(ILogger logger, Exception exception, Guid taskId);
}
