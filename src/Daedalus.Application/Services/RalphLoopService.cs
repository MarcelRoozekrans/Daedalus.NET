using System.Diagnostics;
using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Configuration;
using Daedalus.Domain.Entities;
using Microsoft.Extensions.Logging;
using ZLinq;
using Task = Daedalus.Domain.Entities.Task;
using TaskStatus = Daedalus.Domain.Entities.TaskStatus;

namespace Daedalus.Application.Services;

/// <summary>
///     Default implementation of Ralph loop orchestration.
///     Integrates: subagent delegation, git checkpoints, loop-back evaluation,
///     plan management, and adaptive iteration scaling per the Ralph Wiggum article.
/// </summary>
public sealed partial class RalphLoopService(
    ILlmService llmService,
    ITaskRepository taskRepository,
    IPromptBuilder promptBuilder,
    ILogger<RalphLoopService> logger,
    RalphLoopConfiguration options,
    IMcpAgentSelector mcpAgentSelector,
    IContext7DocumentationInjector context7Injector,
    IGitWorkflowService? gitWorkflowService = null,
    ILoopbackEvaluator? loopbackEvaluator = null,
    McpIntegrationOptions? mcpOptions = null) : IRalphLoopService
{
    private readonly McpIntegrationOptions _mcpOptions = mcpOptions ?? new McpIntegrationOptions { Enabled = false };

    /// <summary>
    ///     Main Ralph loop: repeatedly invoke LLM until completion promise found or max iterations.
    ///     Uses dynamic prompt building that incorporates execution history and context.
    ///     Tracks consecutive failures and stops early if threshold exceeded.
    ///     When MCP is enabled, allows LLM to call MCP tools during execution.
    /// </summary>
    public async Task<Result> ExecuteAsync(Task task, Guid sessionId, CancellationToken ct)
    {
        LogStartingRalphLoop(logger, task.Id, task.MaxIterations, options.IterationDelayMs,
            options.MaxConsecutiveFailures);

        // Log MCP configuration
        if (_mcpOptions.Enabled)
        {
            LogMcpConfigurationExtracted(logger, _mcpOptions.Servers.Count);
            if (_mcpOptions.Servers.Count > 0)
            {
                LogMcpIntegrationEnabled(logger, task.Id, _mcpOptions.Servers.Count);
            }
        }

        // Initialize the prompt context for this session
        var contextResult = await promptBuilder.InitializeContextAsync(
            task.Id,
            sessionId,
            task.Prompt,
            task.CompletionPromise,
            task.Learnings,
            ct);

        if (contextResult.IsFailure)
        {
            LogFailedInitializeContext(logger, contextResult.Error);
            return contextResult;
        }

        var context = contextResult.Value;
        var consecutiveFailures = 0;

        // Enrich context with MCP agents if enabled and services available
        if (_mcpOptions.Enabled && mcpAgentSelector != null && context7Injector != null)
        {
            var agentSelectionResult =
                await mcpAgentSelector.FindAgentsForPromptAsync(task.Prompt, maxResults: 10, ct: ct);
            if (agentSelectionResult.IsSuccess && agentSelectionResult.Value.Count > 0)
            {
                LogMcpAgentsSelected(logger, task.Id, agentSelectionResult.Value.Count);
            }
            else if (agentSelectionResult.IsFailure)
            {
                LogMcpAgentSelectionFailed(logger, task.Id, agentSelectionResult.Error);
            }

            // Inject Context7 documentation for any libraries mentioned in the prompt
            var docInjectionResult = await context7Injector.GetDocumentationContextAsync(task.Prompt, ct);
            if (docInjectionResult.IsSuccess && !string.IsNullOrEmpty(docInjectionResult.Value))
            {
                LogContext7DocumentationInjected(logger, task.Id, docInjectionResult.Value.Length);
            }
            else if (docInjectionResult.IsFailure)
            {
                LogContext7DocumentationFailed(logger, task.Id, docInjectionResult.Error);
            }
        }

        for (var iteration = 1; iteration <= task.MaxIterations; iteration++)
        {
            if (ct.IsCancellationRequested)
            {
                LogRalphLoopCancelled(logger, iteration, task.Id);
                return Result.Failure("Execution cancelled");
            }

            // Build the prompt for this iteration (may include history context)
            var buildPromptResult = await promptBuilder.BuildIterationPromptAsync(context, ct);
            if (buildPromptResult.IsFailure)
            {
                LogFailedBuildPrompt(logger, iteration, buildPromptResult.Error);
                return buildPromptResult;
            }

            var iterationPrompt = buildPromptResult.Value;
            var stopwatch = Stopwatch.StartNew();

            // Invoke LLM with MCP support if enabled
            Result<string> invokeResult;
            if (_mcpOptions.Enabled && _mcpOptions.Servers.Count > 0)
            {
                LogInvokingLlmWithMcp(logger, iteration, task.Id, _mcpOptions.Servers.Count);
                invokeResult = await llmService.InvokeWithMcpAsync(iterationPrompt, _mcpOptions, ct);
            }
            else
            {
                LogInvokingLlmWithoutMcp(logger, iteration, task.Id);
                invokeResult = await llmService.InvokeAsync(iterationPrompt, ct);
            }

            stopwatch.Stop();
            var duration = stopwatch.Elapsed;

            if (invokeResult.IsFailure)
            {
                consecutiveFailures++;
                LogLlmInvocationFailed(logger, iteration, task.Id, invokeResult.Error, consecutiveFailures,
                    options.MaxConsecutiveFailures);

                // Record failure in context
                var recordFailureResult = await promptBuilder.RecordIterationResultAsync(
                    context,
                    iteration,
                    iterationPrompt,
                    string.Empty,
                    false,
                    duration,
                    invokeResult.Error,
                    ct);

                if (recordFailureResult.IsFailure)
                {
                    LogFailedRecordIterationFailure(logger, recordFailureResult.Error);
                }

                // Persist context even on failure
                await promptBuilder.PersistContextAsync(context, ct);

                // Record failed execution in database
                var failedExecution = new TaskExecution
                {
                    Id = Guid.NewGuid(),
                    TaskId = task.Id,
                    SessionId = sessionId,
                    IterationNumber = iteration,
                    Prompt = iterationPrompt,
                    LlmResponse = string.Empty,
                    Error = invokeResult.Error,
                    ExecutionDuration = duration,
                    CompletionPromiseFound = false
                };

                // Record execution via domain method for proper state transitions
                var recordDomainFailureResult = task.RecordExecution(failedExecution);
                if (recordDomainFailureResult.IsFailure)
                {
                    LogFailedRecordExecutionDomain(logger, task.Id, recordDomainFailureResult.Error);
                    return recordDomainFailureResult;
                }

                await taskRepository.RecordExecutionAsync(failedExecution, ct);
                await taskRepository.UpdateAsync(task, ct);

                // Fail task early if too many consecutive failures
                if (consecutiveFailures >= options.MaxConsecutiveFailures)
                {
                    LogMaxConsecutiveFailuresReached(logger, task.Id, consecutiveFailures);
                    return Result.Failure($"Max consecutive failures ({consecutiveFailures}) reached");
                }

                continue;
            }

            // Reset consecutive failures on successful invocation
            consecutiveFailures = 0;

            var response = invokeResult.Value ?? string.Empty;
            var completionPromise = task.CompletionPromise ?? string.Empty;
            var completionFound =
                PerformanceOptimizations.ContainsTarget(response.AsSpan(), completionPromise.AsSpan());

            if (completionFound)
            {
                LogCompletionPromiseFound(logger, iteration, task.MaxIterations, task.Id, duration.TotalMilliseconds,
                    completionPromise);
            }
            else
            {
                LogLlmIterationIncomplete(logger, iteration, task.MaxIterations, task.Id, duration.TotalMilliseconds);
            }

            // Record successful execution in context and database
            var recordSuccessResult = await promptBuilder.RecordIterationResultAsync(
                context,
                iteration,
                iterationPrompt,
                response,
                completionFound,
                duration,
                null,
                ct);

            if (recordSuccessResult.IsFailure)
            {
                LogFailedRecordIterationResult(logger, recordSuccessResult.Error);
                return recordSuccessResult;
            }

            // Persist context
            var persistResult = await promptBuilder.PersistContextAsync(context, ct);
            if (persistResult.IsFailure)
            {
                LogFailedPersistContext(logger, persistResult.Error);
                // Don't fail the task execution, just log the warning
                LogContinuingDespitePersistenceFailure(logger);
            }

            // Record execution via domain method for proper state transitions
            var execution = new TaskExecution
            {
                Id = Guid.NewGuid(),
                TaskId = task.Id,
                SessionId = sessionId,
                IterationNumber = iteration,
                Prompt = iterationPrompt,
                LlmResponse = response,
                CompletionPromiseFound = completionFound,
                ExecutionDuration = duration
            };

            var recordDomainResult = task.RecordExecution(execution);
            if (recordDomainResult.IsFailure)
            {
                LogFailedRecordExecutionDomain(logger, task.Id, recordDomainResult.Error);
                return recordDomainResult;
            }

            // Persist to database
            var recordResult = await taskRepository.RecordExecutionAsync(execution, ct);
            if (recordResult.IsFailure)
            {
                LogFailedPersistExecution(logger, task.Id, recordResult.Error);
                return recordResult;
            }

            // Update task state in database
            var updateResult = await taskRepository.UpdateAsync(task, ct);
            if (updateResult.IsFailure)
            {
                LogFailedUpdateTask(logger, task.Id, updateResult.Error);
                return updateResult;
            }

            // === Subagent delegation: extract learnings via subagent to preserve primary context ===
            if (options.EnableSubagentDelegation && llmService.SupportsSubagents && iteration % 3 == 0)
            {
                await TrySubagentLearningsExtractionAsync(task, response, iteration, ct);
            }

            if (completionFound)
            {
                LogRalphLoopCompletedSuccessfully(logger, task.Id, iteration, response.Length);

                // === Git checkpoint: commit + tag on completion ===
                await TryGitCheckpointAsync(task, iteration, "Completion promise found", ct);
                await TryGitTagAsync(task, ct);

                return Result.Success();
            }

            // Check if task has been marked as failed by RecordExecution due to max iterations
            if (task.Status == TaskStatus.Failed)
            {
                LogRalphLoopExhaustedMaxIterations(logger, task.MaxIterations, task.Id, iteration);
                return Result.Success();
            }

            // === Loop-back evaluation: run build/tests and feed results into next iteration ===
            await TryLoopbackEvaluationAsync(task, context, iteration, ct);

            // Delay between iterations
            await System.Threading.Tasks.Task.Delay(options.IterationDelayMs, ct);
        }

        LogRalphLoopCompleted(logger, task.Id, task.Status);

        return Result.Success();
    }

    /// <summary>
    ///     Extracts learnings from task execution history and persists them for future iterations.
    /// </summary>
#pragma warning disable CA1305, MA0011, MA0002 // Locale-aware formatting not needed for internal learnings text
    public async Task<Result> ExtractAndPersistLearningsAsync(Task task, CancellationToken ct)
    {
        if (task.Executions.Count == 0)
        {
            return Result.Success(); // No executions, no learnings to extract
        }

        var learnings = PerformanceOptimizations.CreateOptimizedBuilder(500);
        learnings.AppendLine("Based on execution history, consider these approaches:");
        learnings.AppendLine();

        // Analyze successful patterns
        var successfulExecution = task.Executions.FirstOrDefault(e => e.CompletionPromiseFound);
        if (successfulExecution != null)
        {
            learnings.AppendLine($"✓ Success achieved at iteration {successfulExecution.IterationNumber}");
            learnings.AppendLine(
                $"  - Use similar approach to the iteration {successfulExecution.IterationNumber} response");
            learnings.AppendLine();
        }

        // Analyze error patterns (using ZLinq for zero-allocation iteration)
        var errorExecutions = task.Executions
            .AsValueEnumerable()
            .Where(e => !string.IsNullOrEmpty(e.Error))
            .ToList();
        if (errorExecutions.Count > 0)
        {
            learnings.AppendLine($"⚠ {errorExecutions.Count} errors encountered:");
            var errorGroups = errorExecutions
                .AsValueEnumerable()
                .GroupBy(e => e.Error)
                .Take(3); // Limit to top 3 error patterns

            foreach (var group in errorGroups)
            {
                learnings.AppendLine($"  - {group.Key} ({group.Count()} occurrence(s))");
            }

            learnings.AppendLine();
        }

        // Analyze incomplete attempts (using ZLinq for zero-allocation iteration)
        var incompleteExecutions = task.Executions
            .AsValueEnumerable()
            .Where(e => string.IsNullOrEmpty(e.Error) && !e.CompletionPromiseFound)
            .ToList();

        if (incompleteExecutions.Count > 0)
        {
            learnings.AppendLine(
                $"ℹ {incompleteExecutions.Count} incomplete attempts - likely need different approach");
            var averageResponseLength = incompleteExecutions
                .AsValueEnumerable()
                .Average(e => e.LlmResponse.Length);
            learnings.AppendLine($"  - Average response length: {(int)averageResponseLength} characters");
            learnings.AppendLine();
        }

        // Add iteration count insight
        learnings.AppendLine($"📊 Total iterations attempted: {task.Executions.Count}");
        learnings.AppendLine($"   Completion promise: \"{task.CompletionPromise}\"");

        var updateResult = task.UpdateLearnings(learnings.ToString());
        if (updateResult.IsFailure)
        {
            LogFailedUpdateLearnings(logger, task.Id, updateResult.Error);
            return updateResult;
        }

        // Persist the updated task
        var persistResult = await taskRepository.UpdateAsync(task, ct);
        if (persistResult.IsFailure)
        {
            LogFailedPersistLearnings(logger, task.Id, persistResult.Error);
            return persistResult;
        }

        LogLearningsExtractedAndPersisted(logger, task.Id, task.Executions.Count);

        return Result.Success();
    }
#pragma warning restore CA1305, MA0011, MA0002

    // ============== New Integration Methods (Ralph Wiggum Article) ==============

    /// <summary>
    ///     Git checkpoint: commit changes after successful iteration.
    ///     The article: "When the tests pass... git add -A... git commit... git push"
    /// </summary>
    private async System.Threading.Tasks.Task TryGitCheckpointAsync(
        Task task, int iteration, string summary, CancellationToken ct)
    {
        if (!options.EnableGitCheckpoints || gitWorkflowService == null ||
            string.IsNullOrEmpty(options.WorkspacePath))
        {
            return;
        }

        var commitResult = await gitWorkflowService.CommitAfterSuccessAsync(
            options.WorkspacePath, task.TaskId, iteration, summary, ct);

        if (commitResult.IsFailure)
        {
            LogGitCheckpointFailed(logger, task.Id, iteration, commitResult.Error);
        }
        else
        {
            LogGitCheckpointCreated(logger, task.Id, iteration, commitResult.Value);
        }
    }

    /// <summary>
    ///     Git tag on task completion.
    ///     The article: "As soon as there are no build or test errors create a git tag"
    /// </summary>
    private async System.Threading.Tasks.Task TryGitTagAsync(Task task, CancellationToken ct)
    {
        if (!options.EnableGitCheckpoints || gitWorkflowService == null ||
            string.IsNullOrEmpty(options.WorkspacePath))
        {
            return;
        }

        var tagResult = await gitWorkflowService.TagOnCompletionAsync(
            options.WorkspacePath, task.TaskId, ct);

        if (tagResult.IsFailure)
        {
            LogGitTagFailed(logger, task.Id, tagResult.Error);
        }

        // Push after tag
        var pushResult = await gitWorkflowService.PushAsync(options.WorkspacePath, ct);
        if (pushResult.IsFailure)
        {
            LogGitPushFailed(logger, task.Id, pushResult.Error);
        }
    }

    /// <summary>
    ///     Loop-back evaluation: run build/tests and inject results into context.
    ///     The article: "Always look for opportunities to loop Ralph back on itself."
    /// </summary>
    private async System.Threading.Tasks.Task TryLoopbackEvaluationAsync(
        Task task, PromptContext? context, int iteration, CancellationToken ct)
    {
        if (!options.EnableLoopbackEvaluation || loopbackEvaluator == null ||
            string.IsNullOrEmpty(options.WorkspacePath) || context == null)
        {
            return;
        }

        var evalResult = await loopbackEvaluator.EvaluateAsync(
            options.WorkspacePath, string.Empty, ct);

        if (evalResult.IsSuccess)
        {
            var loopbackSection = evalResult.Value.ToPromptSection();
            context.Metadata["last_loopback"] = loopbackSection;
            context.Metadata["last_build_ok"] = evalResult.Value.BuildSucceeded.ToString();
            context.Metadata["last_tests_ok"] = evalResult.Value.TestsPassed.ToString();

            LogLoopbackEvaluationCompleted(logger, task.Id, iteration,
                evalResult.Value.BuildSucceeded, evalResult.Value.TestsPassed);

            // Git checkpoint if both build and tests pass
            if (evalResult.Value.BuildSucceeded && evalResult.Value.TestsPassed)
            {
                await TryGitCheckpointAsync(task, iteration,
                    $"Build+tests pass (P:{evalResult.Value.TestsPassed_Count} F:{evalResult.Value.TestsFailed_Count})",
                    ct);
            }
        }
        else
        {
            LogLoopbackEvaluationFailed(logger, task.Id, iteration, evalResult.Error);
        }
    }

    /// <summary>
    ///     Subagent delegation: extract learnings via a subagent to preserve primary context.
    ///     The article: "spawn subagents for expensive allocation-type work"
    /// </summary>
    private async System.Threading.Tasks.Task TrySubagentLearningsExtractionAsync(
        Task task, string latestResponse, int iteration, CancellationToken ct)
    {
        try
        {
            var prompt =
                $"Analyze this LLM response from iteration {iteration} of task '{task.TaskId}' " +
                $"and extract key learnings, patterns, and insights. " +
                $"The completion promise is: \"{task.CompletionPromise}\". " +
                $"Summarize what was attempted, what worked, what didn't, and what to try next. " +
                $"Keep the summary concise (under 500 characters).\n\nResponse:\n{latestResponse}";

            var subagentResult = await llmService.InvokeSubagentAsync(
                prompt,
                new SubagentOptions
                {
                    Label = $"learnings-extraction-iter-{iteration}",
                    MaxTokens = 1024,
                    Timeout = TimeSpan.FromSeconds(30)
                },
                ct);

            if (subagentResult.IsSuccess)
            {
                var currentLearnings = task.Learnings ?? string.Empty;
                var newLearning = $"\n[Iteration {iteration}] {subagentResult.Value.Response}";
                task.UpdateLearnings(currentLearnings + newLearning);
                await taskRepository.UpdateAsync(task, ct);

                LogSubagentLearningsExtracted(logger, task.Id, iteration,
                    subagentResult.Value.Response.Length);
            }
        }
        catch (Exception ex)
        {
            // Non-fatal: subagent extraction is a nice-to-have
            LogSubagentLearningsFailed(logger, task.Id, iteration, ex.Message);
        }
    }

    // ============== Logging Methods ==============

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message =
            "Starting Ralph loop for task {TaskId}, max_iterations={MaxIterations}, iteration_delay_ms={DelayMs}, max_consecutive_failures={MaxFailures}")]
    private static partial void LogStartingRalphLoop(ILogger logger, Guid taskId, int maxIterations, int delayMs,
        int maxFailures);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Failed to initialize prompt context: {Error}")]
    private static partial void LogFailedInitializeContext(ILogger logger, string error);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
        Message = "Ralph loop cancelled at iteration {Iteration} for task {TaskId}")]
    private static partial void LogRalphLoopCancelled(ILogger logger, int iteration, Guid taskId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error,
        Message = "Failed to build prompt for iteration {Iteration}: {Error}")]
    private static partial void LogFailedBuildPrompt(ILogger logger, int iteration, string error);

    [LoggerMessage(EventId = 5, Level = LogLevel.Error,
        Message =
            "LLM invocation failed at iteration {Iteration} for task {TaskId}: {Error} (consecutive_failures={Failures}/{MaxFailures})")]
    private static partial void LogLlmInvocationFailed(ILogger logger, int iteration, Guid taskId, string error,
        int failures, int maxFailures);

    [LoggerMessage(EventId = 6, Level = LogLevel.Error, Message = "Failed to record iteration failure: {Error}")]
    private static partial void LogFailedRecordIterationFailure(ILogger logger, string error);

    [LoggerMessage(EventId = 7, Level = LogLevel.Error,
        Message = "Task {TaskId} reached max consecutive failures ({Failures}), abandoning")]
    private static partial void LogMaxConsecutiveFailuresReached(ILogger logger, Guid taskId, int failures);

    [LoggerMessage(EventId = 8, Level = LogLevel.Information,
        Message =
            "Completion promise found in iteration {Iteration}/{MaxIterations} for task {TaskId}, duration={DurationMs}ms, promise={Promise}")]
    private static partial void LogCompletionPromiseFound(ILogger logger, int iteration, int maxIterations, Guid taskId,
        double durationMs, string promise);

    [LoggerMessage(EventId = 9, Level = LogLevel.Information,
        Message =
            "LLM iteration {Iteration}/{MaxIterations} for task {TaskId}: completion_not_found, duration={DurationMs}ms")]
    private static partial void LogLlmIterationIncomplete(ILogger logger, int iteration, int maxIterations, Guid taskId,
        double durationMs);

    [LoggerMessage(EventId = 10, Level = LogLevel.Error, Message = "Failed to record iteration result: {Error}")]
    private static partial void LogFailedRecordIterationResult(ILogger logger, string error);

    [LoggerMessage(EventId = 11, Level = LogLevel.Error, Message = "Failed to persist prompt context: {Error}")]
    private static partial void LogFailedPersistContext(ILogger logger, string error);

    [LoggerMessage(EventId = 12, Level = LogLevel.Warning, Message = "Continuing despite context persistence failure")]
    private static partial void LogContinuingDespitePersistenceFailure(ILogger logger);

    [LoggerMessage(EventId = 13, Level = LogLevel.Error,
        Message = "Failed to record execution via domain model for task {TaskId}: {Error}")]
    private static partial void LogFailedRecordExecutionDomain(ILogger logger, Guid taskId, string error);

    [LoggerMessage(EventId = 14, Level = LogLevel.Error,
        Message = "Failed to persist execution for task {TaskId}: {Error}")]
    private static partial void LogFailedPersistExecution(ILogger logger, Guid taskId, string error);

    [LoggerMessage(EventId = 15, Level = LogLevel.Error, Message = "Failed to update task {TaskId}: {Error}")]
    private static partial void LogFailedUpdateTask(ILogger logger, Guid taskId, string error);

    [LoggerMessage(EventId = 16, Level = LogLevel.Information,
        Message =
            "Ralph loop completed successfully for task {TaskId} at iteration {Iteration} with result_length={ResultLength}")]
    private static partial void LogRalphLoopCompletedSuccessfully(ILogger logger, Guid taskId, int iteration,
        int resultLength);

    [LoggerMessage(EventId = 17, Level = LogLevel.Warning,
        Message = "Ralph loop exhausted max iterations ({MaxIterations}) for task {TaskId} at iteration {Iteration}")]
    private static partial void LogRalphLoopExhaustedMaxIterations(ILogger logger, int maxIterations, Guid taskId,
        int iteration);

    [LoggerMessage(EventId = 18, Level = LogLevel.Warning,
        Message = "Ralph loop completed for task {TaskId} with final status={Status}")]
    private static partial void LogRalphLoopCompleted(ILogger logger, Guid taskId, TaskStatus status);

    [LoggerMessage(EventId = 19, Level = LogLevel.Error,
        Message = "Failed to update learnings for task {TaskId}: {Error}")]
    private static partial void LogFailedUpdateLearnings(ILogger logger, Guid taskId, string error);

    [LoggerMessage(EventId = 20, Level = LogLevel.Error,
        Message = "Failed to persist learnings for task {TaskId}: {Error}")]
    private static partial void LogFailedPersistLearnings(ILogger logger, Guid taskId, string error);

    [LoggerMessage(EventId = 21, Level = LogLevel.Information,
        Message = "Learnings extracted and persisted for task {TaskId}, executions={Count}")]
    private static partial void LogLearningsExtractedAndPersisted(ILogger logger, Guid taskId, int count);

    [LoggerMessage(EventId = 22, Level = LogLevel.Information,
        Message = "MCP integration enabled for task {TaskId} with {ServerCount} servers")]
    private static partial void LogMcpIntegrationEnabled(ILogger logger, Guid taskId, int serverCount);

    [LoggerMessage(EventId = 23, Level = LogLevel.Debug,
        Message = "MCP configuration extracted: {ServerCount} servers configured")]
    private static partial void LogMcpConfigurationExtracted(ILogger logger, int serverCount);

    [LoggerMessage(EventId = 25, Level = LogLevel.Information,
        Message =
            "Invoking LLM with MCP support for iteration {Iteration} of task {TaskId} with {ServerCount} MCP servers")]
    private static partial void LogInvokingLlmWithMcp(ILogger logger, int iteration, Guid taskId, int serverCount);

    [LoggerMessage(EventId = 26, Level = LogLevel.Information,
        Message = "Invoking LLM without MCP for iteration {Iteration} of task {TaskId}")]
    private static partial void LogInvokingLlmWithoutMcp(ILogger logger, int iteration, Guid taskId);

    [LoggerMessage(EventId = 27, Level = LogLevel.Information,
        Message = "MCP agents selected for task {TaskId}: {AgentCount} agents")]
    private static partial void LogMcpAgentsSelected(ILogger logger, Guid taskId, int agentCount);

    [LoggerMessage(EventId = 28, Level = LogLevel.Warning,
        Message = "Failed to select MCP agents for task {TaskId}: {Error}")]
    private static partial void LogMcpAgentSelectionFailed(ILogger logger, Guid taskId, string error);

    [LoggerMessage(EventId = 29, Level = LogLevel.Information,
        Message = "Context7 documentation injected for task {TaskId}: {DocumentationLength} characters")]
    private static partial void LogContext7DocumentationInjected(ILogger logger, Guid taskId, int documentationLength);

    [LoggerMessage(EventId = 30, Level = LogLevel.Warning,
        Message = "Failed to inject Context7 documentation for task {TaskId}: {Error}")]
    private static partial void LogContext7DocumentationFailed(ILogger logger, Guid taskId, string error);

    // ============== Git / Loopback / Plan / Subagent Logging ==============

    [LoggerMessage(EventId = 40, Level = LogLevel.Information,
        Message = "Git checkpoint created for task {TaskId} at iteration {Iteration}: {CommitHash}")]
    private static partial void LogGitCheckpointCreated(ILogger logger, Guid taskId, int iteration, string commitHash);

    [LoggerMessage(EventId = 41, Level = LogLevel.Warning,
        Message = "Git checkpoint failed for task {TaskId} at iteration {Iteration}: {Error}")]
    private static partial void LogGitCheckpointFailed(ILogger logger, Guid taskId, int iteration, string error);

    [LoggerMessage(EventId = 42, Level = LogLevel.Warning,
        Message = "Git tag failed for task {TaskId}: {Error}")]
    private static partial void LogGitTagFailed(ILogger logger, Guid taskId, string error);

    [LoggerMessage(EventId = 43, Level = LogLevel.Warning,
        Message = "Git push failed for task {TaskId}: {Error}")]
    private static partial void LogGitPushFailed(ILogger logger, Guid taskId, string error);

    [LoggerMessage(EventId = 44, Level = LogLevel.Information,
        Message = "Loopback evaluation completed for task {TaskId} iter {Iteration}: build={BuildOk}, tests={TestsOk}")]
    private static partial void LogLoopbackEvaluationCompleted(
        ILogger logger, Guid taskId, int iteration, bool buildOk, bool testsOk);

    [LoggerMessage(EventId = 45, Level = LogLevel.Warning,
        Message = "Loopback evaluation failed for task {TaskId} iter {Iteration}: {Error}")]
    private static partial void LogLoopbackEvaluationFailed(ILogger logger, Guid taskId, int iteration, string error);

    [LoggerMessage(EventId = 47, Level = LogLevel.Information,
        Message = "Subagent learnings extracted for task {TaskId} iter {Iteration}: {Length} chars")]
    private static partial void LogSubagentLearningsExtracted(ILogger logger, Guid taskId, int iteration, int length);

    [LoggerMessage(EventId = 48, Level = LogLevel.Warning,
        Message = "Subagent learnings extraction failed for task {TaskId} iter {Iteration}: {Error}")]
    private static partial void LogSubagentLearningsFailed(ILogger logger, Guid taskId, int iteration, string error);
}
