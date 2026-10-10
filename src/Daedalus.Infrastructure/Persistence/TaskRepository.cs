using Daedalus.Application.Abstractions;
using Daedalus.Application.Services;
using Daedalus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Results;
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
            dbContext.Tasks.Add(task);
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result<Task>.Success(task);
        }
        catch (Exception ex)
        {
            LogErrorAddingTask(logger, ex, task.Id);
            return Result<Task>.Failure($"Error adding task {task.Id}. The cause is logged.");
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
        catch (DbUpdateConcurrencyException ex) when (ex.Entries.Any(e => e.Entity is Task))
        {
            DetachTasks(ex);
            return Result.Failure(TaskRunGuard.ChangedUnderneath(task.Id));
        }
        catch (Exception ex)
        {
            LogErrorUpdatingTask(logger, ex, task.Id);
            return Result.Failure($"Error updating task {task.Id}. The cause is logged.");
        }
    }

    public async Task<Result> DeleteAsync(Task task, CancellationToken ct)
    {
        try
        {
            // Delete the instance the caller read, so the row version it carries is the original value. A task that
            // gained a run after that read is refused rather than deleted. Setting the state marks this entity only,
            // and the database cascade removes its executions.
            dbContext.Entry(task).State = EntityState.Deleted;
            var saved = await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            return saved > 0
                ? Result.Success()
                : Result.Failure($"Task with ID {task.Id} not found");
        }
        catch (DbUpdateConcurrencyException ex) when (ex.Entries.Any(e => e.Entity is Task))
        {
            DetachTasks(ex);
            return Result.Failure(TaskRunGuard.ChangedUnderneath(task.Id));
        }
        catch (Exception ex)
        {
            LogErrorDeletingTask(logger, ex, task.Id);
            return Result.Failure($"Error deleting task {task.Id}. The cause is logged.");
        }
    }

    /// <summary>Detaches the tasks of a lost race, so the context holds no stale copy; other entries stay tracked.</summary>
    private static void DetachTasks(DbUpdateConcurrencyException ex)
    {
        foreach (var entry in ex.Entries.Where(e => e.Entity is Task))
        {
            entry.State = EntityState.Detached;
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

            return Result<IReadOnlyList<Task>>.Success((IReadOnlyList<Task>)tasks);
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
