using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Configuration;
using Daedalus.Domain.Entities;
using Microsoft.Extensions.Logging;
using ZLinq;
using Task = Daedalus.Domain.Entities.Task;

namespace Daedalus.Application.Services;

/// <summary>
///     Orchestrates the Ralph loop by running middleware for each iteration.
///     Replaces the monolithic ExecuteAsync from RalphLoopService with a composable pipeline.
///     Each iteration flows through ordered middleware stages that handle distinct concerns.
/// </summary>
public sealed partial class RalphLoopPipelineService(
    IEnumerable<IRalphLoopMiddleware> middlewares,
    IPromptBuilder promptBuilder,
    ITaskRepository taskRepository,
    ILearningsService learningsService,
    ILogger<RalphLoopPipelineService> logger,
    RalphLoopConfiguration options) : IRalphLoopService
{
    private readonly IRalphLoopMiddleware[] _pipeline = middlewares
        .AsValueEnumerable()
        .OrderBy(m => m.Order)
        .ToArray();

    public async Task<Result> ExecuteAsync(Task task, Guid sessionId, CancellationToken ct)
    {
        LogStartingRalphLoop(logger, task.Id, task.MaxIterations, options.IterationDelayMs,
            options.MaxConsecutiveFailures);

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

        var promptContext = contextResult.Value;
        var maxIter = task.MaxIterations > 0 ? task.MaxIterations : options.MaxIterations;

        for (var iteration = 1; maxIter == 0 || iteration <= maxIter; iteration++)
        {
            ct.ThrowIfCancellationRequested();

            var iterContext = new RalphIterationContext
            {
                Task = task,
                SessionId = sessionId,
                PromptContext = promptContext,
                Iteration = iteration,
                Configuration = options
            };

            // Execute the middleware pipeline
            var pipelineResult = await ExecutePipelineAsync(iterContext, ct);
            if (pipelineResult.IsFailure)
            {
                LogPipelineExecutionFailed(logger, iteration, task.Id, pipelineResult.Error);
                return pipelineResult;
            }

            // Record iteration to database regardless of outcome
            var recordResult = await RecordIterationAsync(task, sessionId, iterContext, ct);
            if (recordResult.IsFailure)
            {
                LogFailedRecordIteration(logger, iteration, recordResult.Error);
                return recordResult;
            }

            // Check termination conditions
            if (iterContext.ShouldTerminateLoop)
            {
                LogRalphLoopTerminated(logger, task.Id, iterContext.TerminationReason);
                break;
            }

            if (iterContext.CompletionPromiseFound)
            {
                LogCompletionPromiseFound(logger, iteration, task.MaxIterations, task.Id,
                    iterContext.InvocationDuration.TotalMilliseconds, task.CompletionPromise);
                break;
            }

            // Delay between iterations
            if (options.IterationDelayMs > 0)
            {
                await System.Threading.Tasks.Task.Delay(options.IterationDelayMs, ct);
            }
        }

        var statusText = task.Status.ToString();
        LogRalphLoopCompleted(logger, task.Id, statusText);
        return Result.Success();
    }

    [SuppressMessage("Globalization", "CA1305",
        Justification = "Locale-aware formatting not required for internal learnings text")]
    public async Task<Result> ExtractAndPersistLearningsAsync(Task task, CancellationToken ct)
    {
        if (task.Executions.Count == 0)
        {
            return Result.Success(); // No executions, no learnings to extract
        }

        var learnings = new StringBuilder();
        learnings.Append("Based on execution history, consider these approaches:");
        learnings.Append(Environment.NewLine);
        learnings.Append(Environment.NewLine);

        // Analyze successful patterns
        var successfulExecution = task.Executions.FirstOrDefault(e => e.CompletionPromiseFound);
        if (successfulExecution != null)
        {
            learnings.Append(CultureInfo.InvariantCulture,
                $"✓ Success achieved at iteration {successfulExecution.IterationNumber}");
            learnings.Append(Environment.NewLine);
            learnings.Append(CultureInfo.InvariantCulture,
                $"  - Use similar approach to the iteration {successfulExecution.IterationNumber} response");
            learnings.Append(Environment.NewLine);
            learnings.Append(Environment.NewLine);
        }

        // Analyze error patterns (using ZLinq for zero-allocation iteration)
        var errorExecutions = task.Executions
            .AsValueEnumerable()
            .Where(e => !string.IsNullOrEmpty(e.Error))
            .ToList();
        if (errorExecutions.Count > 0)
        {
            learnings.Append(CultureInfo.InvariantCulture, $"⚠ {errorExecutions.Count} errors encountered:");
            learnings.Append(Environment.NewLine);
            var errorGroups = errorExecutions
                .AsValueEnumerable()
                .GroupBy(e => e.Error, StringComparer.Ordinal)
                .Take(3); // Limit to top 3 error patterns

            foreach (var group in errorGroups)
            {
                learnings.Append(CultureInfo.InvariantCulture, $"  - {group.Key} ({group.Count()} occurrence(s))");
                learnings.Append(Environment.NewLine);
            }

            learnings.Append(Environment.NewLine);
        }

        // Analyze incomplete attempts (using ZLinq for zero-allocation iteration)
        var incompleteExecutions = task.Executions
            .AsValueEnumerable()
            .Where(e => string.IsNullOrEmpty(e.Error) && !e.CompletionPromiseFound)
            .ToList();

        if (incompleteExecutions.Count > 0)
        {
            learnings.Append(CultureInfo.InvariantCulture,
                $"ℹ {incompleteExecutions.Count} incomplete attempts - likely need different approach");
            learnings.Append(Environment.NewLine);
            var averageResponseLength = incompleteExecutions
                .AsValueEnumerable()
                .Average(e => e.LlmResponse.Length);
            learnings.Append(CultureInfo.InvariantCulture,
                $"  - Average response length: {(int)averageResponseLength} characters");
            learnings.Append(Environment.NewLine);
            learnings.Append(Environment.NewLine);
        }

        // Add iteration count insight
        learnings.Append(CultureInfo.InvariantCulture, $"📊 Total iterations attempted: {task.Executions.Count}");
        learnings.Append(Environment.NewLine);
        learnings.Append(CultureInfo.InvariantCulture, $"   Completion promise: \"{task.CompletionPromise}\"");

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

        // Also persist as structured learnings for cross-task knowledge transfer
        var projectId = task.ProjectId != Guid.Empty ? task.ProjectId : (Guid?)null;
        var structuredResult = await learningsService.ParseAndPersistLearningsAsync(
            learnings.ToString(), task.Id, projectId, ct);
        if (structuredResult.IsFailure)
        {
            logger.LogWarning(
                "Failed to persist structured learnings for task {TaskId}: {Error}",
                task.Id, structuredResult.Error);
            // Non-fatal — raw learnings already persisted
        }

        return Result.Success();
    }

    /// <summary>
    ///     Executes the middleware pipeline for a single iteration.
    ///     Builds the chain inside-out so each middleware can call continuation() naturally.
    /// </summary>
    private async Task<Result> ExecutePipelineAsync(RalphIterationContext context, CancellationToken ct)
    {
        var index = _pipeline.Length - 1;

        Func<Task<Result>> BuildChain(int i)
        {
            if (i < 0)
            {
                return () => System.Threading.Tasks.Task.FromResult(Result.Success());
            }

            return () => _pipeline[i].InvokeAsync(context, BuildChain(i - 1), ct);
        }

        return await BuildChain(index)();
    }

    /// <summary>
    ///     Records the iteration results to the database and updates task state.
    /// </summary>
    private async Task<Result> RecordIterationAsync(
        Task task, Guid sessionId, RalphIterationContext context, CancellationToken ct)
    {
        try
        {
            // Persist prompt context
            var persistResult = await promptBuilder.PersistContextAsync(context.PromptContext, ct);
            if (persistResult.IsFailure)
            {
                LogFailedPersistContext(logger, persistResult.Error);
                // Non-fatal — continue execution
            }

            // Create execution record
            var execution = new TaskExecution
            {
                Id = Guid.NewGuid(),
                TaskId = task.Id,
                SessionId = sessionId,
                IterationNumber = context.Iteration,
                Prompt = context.IterationPrompt ?? string.Empty,
                LlmResponse = context.LlmResponse ?? string.Empty,
                Error = context.LlmInvocationSucceeded ? null : "LLM invocation failed",
                ExecutionDuration = context.InvocationDuration,
                CompletionPromiseFound = context.CompletionPromiseFound
            };

            // Record execution in domain
            var recordDomainResult = task.RecordExecution(execution);
            if (recordDomainResult.IsFailure)
            {
                LogFailedRecordExecutionDomain(logger, task.Id, recordDomainResult.Error);
                return recordDomainResult;
            }

            // Record in database
            var recordDbResult = await taskRepository.RecordExecutionAsync(execution, ct);
            if (recordDbResult.IsFailure)
            {
                LogFailedPersistExecution(logger, task.Id, recordDbResult.Error);
                return recordDbResult;
            }

            // Update task
            var updateResult = await taskRepository.UpdateAsync(task, ct);
            if (updateResult.IsFailure)
            {
                LogFailedUpdateTask(logger, task.Id, updateResult.Error);
                return updateResult;
            }

            return Result.Success();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error recording iteration {Iteration}", context.Iteration);
            return Result.Failure($"Failed to record iteration: {ex.Message}");
        }
    }

    // ============== Logging Methods ==============

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message =
            "Starting Ralph loop for task {TaskId}, max iterations: {MaxIterations}, delay: {DelayMs}ms, max failures: {MaxFailures}")]
    private static partial void LogStartingRalphLoop(
        ILogger logger, Guid taskId, int maxIterations, int delayMs, int maxFailures);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Error,
        Message = "Failed to initialize prompt context: {Error}")]
    private static partial void LogFailedInitializeContext(ILogger logger, string error);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Error,
        Message = "Pipeline execution failed at iteration {Iteration} for task {TaskId}: {Error}")]
    private static partial void LogPipelineExecutionFailed(
        ILogger logger, int iteration, Guid taskId, string error);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Error,
        Message = "Failed to record iteration {Iteration}: {Error}")]
    private static partial void LogFailedRecordIteration(ILogger logger, int iteration, string error);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Information,
        Message = "Ralph loop terminated for task {TaskId}: {Reason}")]
    private static partial void LogRalphLoopTerminated(
        ILogger logger, Guid taskId, string? reason);

    [LoggerMessage(
        EventId = 6,
        Level = LogLevel.Information,
        Message =
            "Completion promise found at iteration {Iteration}/{MaxIterations} for task {TaskId}, duration: {DurationMs}ms, promise: {Promise}")]
    private static partial void LogCompletionPromiseFound(
        ILogger logger, int iteration, int maxIterations, Guid taskId, double durationMs, string promise);

    [LoggerMessage(
        EventId = 7,
        Level = LogLevel.Information,
        Message = "Ralph loop completed for task {TaskId}, status: {Status}")]
    private static partial void LogRalphLoopCompleted(ILogger logger, Guid taskId, string status);

    [LoggerMessage(
        EventId = 8,
        Level = LogLevel.Error,
        Message = "Failed to persist prompt context: {Error}")]
    private static partial void LogFailedPersistContext(ILogger logger, string error);

    [LoggerMessage(
        EventId = 9,
        Level = LogLevel.Error,
        Message = "Failed to record execution in domain for task {TaskId}: {Error}")]
    private static partial void LogFailedRecordExecutionDomain(ILogger logger, Guid taskId, string error);

    [LoggerMessage(
        EventId = 10,
        Level = LogLevel.Error,
        Message = "Failed to persist execution for task {TaskId}: {Error}")]
    private static partial void LogFailedPersistExecution(ILogger logger, Guid taskId, string error);

    [LoggerMessage(
        EventId = 11,
        Level = LogLevel.Error,
        Message = "Failed to update task {TaskId}: {Error}")]
    private static partial void LogFailedUpdateTask(ILogger logger, Guid taskId, string error);

    [LoggerMessage(
        EventId = 12,
        Level = LogLevel.Error,
        Message = "Failed to update learnings for task {TaskId}: {Error}")]
    private static partial void LogFailedUpdateLearnings(ILogger logger, Guid taskId, string error);

    [LoggerMessage(
        EventId = 13,
        Level = LogLevel.Error,
        Message = "Failed to persist learnings for task {TaskId}: {Error}")]
    private static partial void LogFailedPersistLearnings(ILogger logger, Guid taskId, string error);

    [LoggerMessage(
        EventId = 14,
        Level = LogLevel.Information,
        Message = "Learnings extracted and persisted for task {TaskId} from {ExecutionCount} executions")]
    private static partial void LogLearningsExtractedAndPersisted(
        ILogger logger, Guid taskId, int executionCount);
}
