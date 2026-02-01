using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Infrastructure.Persistence;

/// <summary>
///     Database-backed prompt context store that reconstructs PromptContext from
///     existing Task + TaskExecution records instead of writing JSON files to disk.
///     Eliminates file I/O and disk dependency — data is already persisted by the
///     pipeline's RecordIterationAsync flow.
/// </summary>
public sealed partial class DatabasePromptContextStore(
    ApplicationDbContext dbContext,
    ILogger<DatabasePromptContextStore> logger) : IPromptContextStore
{
    /// <summary>
    ///     Save is a lightweight operation — the heavy data (TaskExecution records, Task state)
    ///     is already persisted by the pipeline. We only log for observability.
    /// </summary>
    public Task<Result> SaveAsync(PromptContext context, CancellationToken ct)
    {
        // TaskExecution records and Task state are already persisted per-iteration
        // by RalphLoopPipelineService.RecordIterationAsync and RalphLoopService.ExecuteAsync.
        // No separate file write needed — the DB is the source of truth.
        LogContextSaved(logger, context.TaskId, context.SessionId, context.Iteration);
        return Task.FromResult(Result.Success());
    }

    /// <summary>
    ///     Reconstructs PromptContext from existing DB records for crash recovery.
    ///     Builds history from TaskExecution records and loads learnings from Task entity.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "EF Core query materialization; trimming safe for known entity types.")]
    public async Task<Result<PromptContext>> LoadAsync(Guid taskId, Guid sessionId, CancellationToken ct)
    {
        try
        {
            // Load the task with its executions for this session
            var task = await dbContext.Set<Domain.Entities.Task>()
                .AsNoTracking()
                .Include(t => t.Executions)
                .FirstOrDefaultAsync(t => t.Id == taskId, ct).ConfigureAwait(false);

            if (task is null)
            {
                return Result.Failure<PromptContext>($"Task {taskId} not found in database");
            }

            // Get executions for this specific session, ordered by iteration
            var sessionExecutions = task.Executions
                .Where(e => e.SessionId == sessionId)
                .OrderBy(e => e.IterationNumber)
                .ToList();

            // Reconstruct the PromptContext
            var context = new PromptContext
            {
                Id = Guid.NewGuid(),
                TaskId = taskId,
                SessionId = sessionId,
                Iteration = sessionExecutions.Count,
                OriginalPrompt = task.Prompt,
                CompletionPromise = task.CompletionPromise,
                AccumulatedLearnings = task.Learnings,
                CreatedAt = task.CreatedAt,
                UpdatedAt = DateTime.UtcNow,
                Metadata =
                {
                    ["recovered_from_db"] = "true",
                    ["total_iterations"] = sessionExecutions.Count.ToString(CultureInfo.InvariantCulture),
                    ["consecutive_failures"] = "0" // Reset on recovery
                }
            };

            // Reconstruct history from TaskExecution records
            foreach (var execution in sessionExecutions)
            {
                var entry = new PromptHistoryEntry
                {
                    Iteration = execution.IterationNumber,
                    Prompt = string.Empty, // Don't reload full prompts — use compact approach to minimize memory
                    Response = string.Empty, // Don't reload full responses
                    CompletionPromiseFound = execution.CompletionPromiseFound,
                    Duration = execution.ExecutionDuration,
                    ExecutionError = execution.Error,
                    ExecutedAt = execution.ExecutedAt,
                    CompactSummary = PromptHistoryEntry.BuildCompactSummary(
                        execution.LlmResponse,
                        execution.Error,
                        execution.CompletionPromiseFound,
                        execution.ExecutionDuration)
                };

                context.History.Add(entry);
            }

            LogContextLoaded(logger, taskId, sessionId, context.Iteration, sessionExecutions.Count);
            return Result.Success(context);
        }
        catch (Exception ex)
        {
            LogContextLoadError(logger, taskId, ex.Message);
            return Result.Failure<PromptContext>($"Failed to load prompt context from DB: {ex.Message}");
        }
    }

    /// <summary>
    ///     Delete is a no-op — TaskExecution records are cleaned up as part of Task lifecycle.
    /// </summary>
    public Task<Result> DeleteAsync(Guid taskId, Guid sessionId, CancellationToken ct)
    {
        LogContextDeleted(logger, taskId, sessionId);
        return Task.FromResult(Result.Success());
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
        Message =
            "Prompt context saved (DB-backed, no-op) for task {TaskId}, session {SessionId}, iteration {Iteration}")]
    private static partial void LogContextSaved(ILogger logger, Guid taskId, Guid sessionId, int iteration);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message =
            "Prompt context reconstructed from DB for task {TaskId}, session {SessionId}, iteration {Iteration}, executions={ExecutionCount}")]
    private static partial void LogContextLoaded(ILogger logger, Guid taskId, Guid sessionId, int iteration,
        int executionCount);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug,
        Message = "Prompt context delete (DB-backed, no-op) for task {TaskId}, session {SessionId}")]
    private static partial void LogContextDeleted(ILogger logger, Guid taskId, Guid sessionId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error,
        Message = "Failed to load prompt context from DB for task {TaskId}: {Error}")]
    private static partial void LogContextLoadError(ILogger logger, Guid taskId, string error);
}
