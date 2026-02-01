using System.Globalization;
using System.Text;
using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Task = Daedalus.Domain.Entities.Task;
using TaskStatus = Daedalus.Domain.Entities.TaskStatus;

namespace Daedalus.Infrastructure.Persistence;

/// <summary>
///     Task repository with distributed locking support.
/// </summary>
public sealed partial class TaskRepository(ApplicationDbContext dbContext, ILogger<TaskRepository> logger)
    : ITaskRepository
{
    public async Task<Result<Task>> GetByIdAsync(Guid id, CancellationToken ct)
    {
        try
        {
            var task = await dbContext.Tasks
                .AsNoTracking()
                .Include(t => t.Executions)
                .FirstOrDefaultAsync(t => t.Id == id, ct)
                .ConfigureAwait(false);

            return task is not null
                ? Result.Success(task)
                : Result.Failure<Task>($"Task {id} not found");
        }
        catch (Exception ex)
        {
            LogErrorRetrievingTask(logger, ex, id);
            return Result.Failure<Task>($"Error retrieving task: {ex.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<Task>>> GetPendingAsync(CancellationToken ct)
    {
        try
        {
            var tasks = await dbContext.Tasks
                .AsNoTracking()
                .Where(t => t.Status == TaskStatus.Pending)
                .OrderBy(t => t.CreatedAt)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            return Result.Success((IReadOnlyList<Task>)tasks);
        }
        catch (Exception ex)
        {
            LogErrorRetrievingPendingTasks(logger, ex);
            return Result.Failure<IReadOnlyList<Task>>($"Error retrieving tasks: {ex.Message}");
        }
    }

    /// <summary>
    ///     Gets pending tasks with database-level pagination (optimized performance).
    ///     Skip/Take are executed at database level instead of in-memory.
    /// </summary>
    public async Task<Result<IReadOnlyList<Task>>> GetPendingAsync(int skip, int take, CancellationToken ct)
    {
        try
        {
            var tasks = await dbContext.Tasks
                .AsNoTracking()
                .Where(t => t.Status == TaskStatus.Pending)
                .OrderByDescending(t => t.CreatedAt)
                .Skip(skip)
                .Take(take)
                .Include(t => t.Executions)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            return Result.Success((IReadOnlyList<Task>)tasks);
        }
        catch (Exception ex)
        {
            LogErrorRetrievingPendingTasks(logger, ex);
            return Result.Failure<IReadOnlyList<Task>>($"Error retrieving paginated tasks: {ex.Message}");
        }
    }

    /// <summary>
    ///     Gets the count of pending tasks efficiently without loading full entities.
    /// </summary>
    public async Task<Result<int>> GetPendingCountAsync(CancellationToken ct)
    {
        try
        {
            var count = await dbContext.Tasks
                .AsNoTracking()
                .Where(t => t.Status == TaskStatus.Pending)
                .CountAsync(ct)
                .ConfigureAwait(false);

            return Result.Success(count);
        }
        catch (Exception ex)
        {
            LogErrorRetrievingPendingTasks(logger, ex);
            return Result.Failure<int>($"Error counting pending tasks: {ex.Message}");
        }
    }

    /// <summary>
    ///     Atomically claims next pending task using SELECT FOR UPDATE SKIP LOCKED.
    ///     Only claims tasks whose dependencies are all satisfied (Completed).
    /// </summary>
    public async Task<Result<Task?>> ClaimNextAsync(Guid sessionId, CancellationToken ct)
    {
        try
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(ct)
                .ConfigureAwait(false);

            // SELECT FOR UPDATE SKIP LOCKED - atomic task claiming
            // Dependency gate: only claim tasks with no dependencies OR all dependencies Completed
            var task = await dbContext.Tasks
                .FromSqlInterpolated($@"
                    SELECT * FROM ""Tasks"" t
                    WHERE t.""Status"" = {(int)TaskStatus.Pending}
                    AND (
                        t.""Dependencies"" IS NULL
                        OR cardinality(t.""Dependencies"") = 0
                        OR NOT EXISTS (
                            SELECT 1 FROM unnest(t.""Dependencies"") AS dep_task_id
                            WHERE NOT EXISTS (
                                SELECT 1 FROM ""Tasks"" dep
                                WHERE dep.""TaskId"" = dep_task_id
                                AND dep.""Status"" = {(int)TaskStatus.Completed}
                            )
                        )
                    )
                    ORDER BY t.""CreatedAt"" ASC
                    LIMIT 1
                    FOR UPDATE SKIP LOCKED")
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            if (task is not null)
            {
                var claimResult = task.Claim(sessionId);
                if (claimResult.IsFailure)
                {
                    await transaction.RollbackAsync(ct).ConfigureAwait(false);
                    return Result.Failure<Task?>(claimResult.Error);
                }

                dbContext.Tasks.Update(task);
                await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);

                LogTaskClaimed(logger, sessionId, task.Id);
            }

            return Result.Success(task);
        }
        catch (Exception ex)
        {
            LogErrorClaimingTask(logger, ex, sessionId);
            return Result.Failure<Task?>($"Error claiming task: {ex.Message}");
        }
    }

    public async Task<Result<Task>> AddAsync(Task task, CancellationToken ct)
    {
        try
        {
            dbContext.Tasks.Add(task);
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result.Success(task);
        }
        catch (Exception ex)
        {
            LogErrorAddingTask(logger, ex, task.Id);
            var exceptionDetails = BuildExceptionDetails(ex);
            return Result.Failure<Task>($"Error adding task: {exceptionDetails}");
        }
    }

    public async Task<Result> UpdateAsync(Task task, CancellationToken ct)
    {
        try
        {
            dbContext.Tasks.Update(task);
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result.Success();
        }
        catch (Exception ex)
        {
            LogErrorUpdatingTask(logger, ex, task.Id);
            return Result.Failure($"Error updating task: {ex.Message}");
        }
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken ct)
    {
        try
        {
            var deleted = await dbContext.Tasks
                .Where(t => t.Id == id)
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);

            return deleted > 0
                ? Result.Success()
                : Result.Failure($"Task with ID {id} not found");
        }
        catch (Exception ex)
        {
            LogErrorUpdatingTask(logger, ex, id);
            return Result.Failure($"Error deleting task: {ex.Message}");
        }
    }

    public async Task<Result> RecordExecutionAsync(TaskExecution execution, CancellationToken ct)
    {
        try
        {
            dbContext.TaskExecutions.Add(execution);
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result.Success();
        }
        catch (Exception ex)
        {
            LogErrorRecordingExecution(logger, ex, execution.TaskId);
            return Result.Failure($"Error recording execution: {ex.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<Task>>> GetStaleInProgressAsync(TimeSpan staleness, CancellationToken ct)
    {
        try
        {
            var cutoffTime = DateTime.UtcNow.Subtract(staleness);

            var staleTasks = await dbContext.Tasks
                .AsNoTracking()
                .Join(
                    dbContext.ExecutionSessions.Where(s => !s.IsActive || s.LastHeartbeat < cutoffTime),
                    t => t.CurrentSessionId,
                    s => s.Id,
                    (t, s) => t)
                .Where(t => t.Status == TaskStatus.InProgress)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            return Result.Success((IReadOnlyList<Task>)staleTasks);
        }
        catch (Exception ex)
        {
            LogErrorRetrievingStale(logger, ex);
            return Result.Failure<IReadOnlyList<Task>>($"Error retrieving stale tasks: {ex.Message}");
        }
    }

    /// <summary>
    ///     Gets all tasks belonging to a specific project for dependency resolution.
    /// </summary>
    public async Task<Result<IReadOnlyList<Task>>> GetByProjectIdAsync(Guid projectId, CancellationToken ct)
    {
        try
        {
            var tasks = await dbContext.Tasks
                .AsNoTracking()
                .Where(t => t.ProjectId == projectId)
                .OrderBy(t => t.CreatedAt)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            return Result.Success((IReadOnlyList<Task>)tasks);
        }
        catch (Exception ex)
        {
            LogErrorRetrievingProjectTasks(logger, ex, projectId);
            return Result.Failure<IReadOnlyList<Task>>($"Error retrieving project tasks: {ex.Message}");
        }
    }

    [LoggerMessage(EventId = 10, Level = LogLevel.Error, Message = "Error retrieving task {TaskId}")]
    private static partial void LogErrorRetrievingTask(ILogger logger, Exception exception, Guid taskId);

    [LoggerMessage(EventId = 11, Level = LogLevel.Error, Message = "Error retrieving pending tasks")]
    private static partial void LogErrorRetrievingPendingTasks(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 12, Level = LogLevel.Information, Message = "Session {SessionId} claimed task {TaskId}")]
    private static partial void LogTaskClaimed(ILogger logger, Guid sessionId, Guid taskId);

    [LoggerMessage(EventId = 13, Level = LogLevel.Error, Message = "Error claiming next task for session {SessionId}")]
    private static partial void LogErrorClaimingTask(ILogger logger, Exception exception, Guid sessionId);

    [LoggerMessage(EventId = 14, Level = LogLevel.Error, Message = "Error adding task {TaskId}")]
    private static partial void LogErrorAddingTask(ILogger logger, Exception exception, Guid taskId);

    [LoggerMessage(EventId = 15, Level = LogLevel.Error, Message = "Error updating task {TaskId}")]
    private static partial void LogErrorUpdatingTask(ILogger logger, Exception exception, Guid taskId);

    [LoggerMessage(EventId = 16, Level = LogLevel.Error, Message = "Error recording execution for task {TaskId}")]
    private static partial void LogErrorRecordingExecution(ILogger logger, Exception exception, Guid taskId);

    [LoggerMessage(EventId = 17, Level = LogLevel.Error, Message = "Error retrieving stale in-progress tasks")]
    private static partial void LogErrorRetrievingStale(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 18, Level = LogLevel.Error,
        Message = "Error retrieving tasks for project {ProjectId}")]
    private static partial void LogErrorRetrievingProjectTasks(ILogger logger, Exception exception, Guid projectId);

    /// <summary>
    ///     Builds detailed exception information including inner exception details.
    /// </summary>
    private static string BuildExceptionDetails(Exception ex)
    {
        var details = new StringBuilder();
        details.AppendLine(CultureInfo.InvariantCulture, $"Main Exception: {ex.GetType().Name} - {ex.Message}");

        if (ex.InnerException is not null)
        {
            details.AppendLine(CultureInfo.InvariantCulture,
                $"Inner Exception: {ex.InnerException.GetType().Name} - {ex.InnerException.Message}");

            if (ex.InnerException.InnerException is not null)
            {
                details.AppendLine(CultureInfo.InvariantCulture,
                    $"Inner Inner Exception: {ex.InnerException.InnerException.GetType().Name} - {ex.InnerException.InnerException.Message}");
            }
        }

        return details.ToString();
    }
}
