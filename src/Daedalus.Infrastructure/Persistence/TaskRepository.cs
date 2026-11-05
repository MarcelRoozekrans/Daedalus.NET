using Daedalus.Application.Abstractions;
using Daedalus.Application.Services;
using Daedalus.Infrastructure.Persistence.Orm;
using Daedalus.Infrastructure.Persistence.Orm.Tasks;
using Microsoft.Extensions.Logging;
using ZeroAlloc.ORM;
using ZeroAlloc.Results;
using Task = Daedalus.Domain.Entities.Task;

namespace Daedalus.Infrastructure.Persistence;

/// <summary>
///     Stores tasks on ZeroAlloc.ORM, one pooled connection per call.
/// </summary>
/// <remarks>
///     <see cref="UpdateAsync"/> and <see cref="DeleteAsync"/> are guarded by the <c>xmin</c> the task was read with,
///     so a save of a task another write changed since is refused with <see cref="TaskRunGuard.ChangedUnderneath"/>.
///     A save does not read the new <c>xmin</c> back into the instance: re-read a task with
///     <see cref="GetByIdAsync"/> after <see cref="AddAsync"/> or <see cref="UpdateAsync"/> before its next guarded
///     save, or that save is refused as changed underneath (ruling R58).
/// </remarks>
public sealed partial class TaskRepository(DaedalusDb db, ILogger<TaskRepository> logger)
    : ITaskRepository
{
    public async Task<Result<Task>> GetByIdAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
            var task = await new TaskSql(connection).FindAsync(id, ct).ConfigureAwait(false);

            return task is not null
                ? Result<Task>.Success(task)
                : Result<Task>.Failure($"Task {id} not found");
        }
        catch (Exception ex)
        {
            LogErrorRetrievingTask(logger, ex, id);
            return Result<Task>.Failure($"Error retrieving task {id}. The cause is logged.");
        }
    }

    public async Task<Result<Task>> AddAsync(Task task, CancellationToken ct)
    {
        try
        {
            await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
            await new TaskSql(connection).InsertAsync(task, ct).ConfigureAwait(false);
            return Result<Task>.Success(task);
        }
        catch (Exception ex)
        {
            LogErrorAddingTask(logger, ex, task.Id);
            return Result<Task>.Failure($"Error adding task {task.Id}. The cause is logged.");
        }
    }

    /// <summary>
    ///     Writes the columns the task's domain methods change, when the row still has the <c>xmin</c> the task was read
    ///     with; otherwise, a row changed or deleted since, answers <see cref="TaskRunGuard.ChangedUnderneath"/>.
    /// </summary>
    public async Task<Result> UpdateAsync(Task task, CancellationToken ct)
    {
        try
        {
            await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
            await new TaskSql(connection).UpdateAsync(task, ct).ConfigureAwait(false);
            return Result.Success();
        }
        catch (ZeroAllocOrmConcurrencyException)
        {
            return Result.Failure(TaskRunGuard.ChangedUnderneath(task.Id));
        }
        catch (Exception ex)
        {
            LogErrorUpdatingTask(logger, ex, task.Id);
            return Result.Failure($"Error updating task {task.Id}. The cause is logged.");
        }
    }

    /// <summary>
    ///     Deletes the task instance that was read: a task no longer stored answers "Task with ID {id} not found"; one
    ///     changed or deleted after this check, or changed since the caller read it, such as one that gained a run,
    ///     is refused with <see cref="TaskRunGuard.ChangedUnderneath"/>. The database cascade removes its executions.
    /// </summary>
    public async Task<Result> DeleteAsync(Task task, CancellationToken ct)
    {
        try
        {
            await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
            var sql = new TaskSql(connection);
            if (!await sql.ExistsAsync(task.Id, ct).ConfigureAwait(false))
            {
                return Result.Failure($"Task with ID {task.Id} not found");
            }

            await sql.DeleteAsync(task, ct).ConfigureAwait(false);
            return Result.Success();
        }
        catch (ZeroAllocOrmConcurrencyException)
        {
            return Result.Failure(TaskRunGuard.ChangedUnderneath(task.Id));
        }
        catch (Exception ex)
        {
            LogErrorDeletingTask(logger, ex, task.Id);
            return Result.Failure($"Error deleting task {task.Id}. The cause is logged.");
        }
    }

    /// <summary>
    ///     Gets all tasks belonging to a specific project for dependency resolution, without their executions.
    /// </summary>
    public async Task<Result<IReadOnlyList<Task>>> GetByProjectIdAsync(Guid projectId, CancellationToken ct)
    {
        try
        {
            await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
            var tasks = await new TaskSql(connection).ListByProjectAsync(projectId, ct).ConfigureAwait(false);
            return Result<IReadOnlyList<Task>>.Success(tasks);
        }
        catch (Exception ex)
        {
            LogErrorRetrievingProjectTasks(logger, ex, projectId);
            return Result<IReadOnlyList<Task>>.Failure($"Error retrieving the tasks of project {projectId}. The cause is logged.");
        }
    }

    [LoggerMessage(EventId = 10, Level = LogLevel.Error, Message = "Error retrieving task {TaskId}")]
    private static partial void LogErrorRetrievingTask(ILogger logger, Exception exception, Guid taskId);

    [LoggerMessage(EventId = 14, Level = LogLevel.Error, Message = "Error adding task {TaskId}")]
    private static partial void LogErrorAddingTask(ILogger logger, Exception exception, Guid taskId);

    [LoggerMessage(EventId = 15, Level = LogLevel.Error, Message = "Error updating task {TaskId}")]
    private static partial void LogErrorUpdatingTask(ILogger logger, Exception exception, Guid taskId);

    [LoggerMessage(EventId = 19, Level = LogLevel.Error, Message = "Error deleting task {TaskId}")]
    private static partial void LogErrorDeletingTask(ILogger logger, Exception exception, Guid taskId);

    [LoggerMessage(EventId = 18, Level = LogLevel.Error,
        Message = "Error retrieving tasks for project {ProjectId}")]
    private static partial void LogErrorRetrievingProjectTasks(ILogger logger, Exception exception, Guid projectId);
}
