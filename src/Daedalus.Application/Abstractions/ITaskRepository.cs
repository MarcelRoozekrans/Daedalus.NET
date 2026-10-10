using Daedalus.Domain.Entities;
using ZeroAlloc.Results;
using Task = Daedalus.Domain.Entities.Task;

namespace Daedalus.Application.Abstractions;

/// <summary>
///     Repository for task persistence and querying.
/// </summary>
public interface ITaskRepository
{
    /// <summary>Gets a task by ID.</summary>
    Task<Result<Task>> GetByIdAsync(Guid id, CancellationToken ct);

    /// <summary>Adds a new task.</summary>
    Task<Result<Task>> AddAsync(Task task, CancellationToken ct);

    /// <summary>Updates an existing task.</summary>
    Task<Result> UpdateAsync(Task task, CancellationToken ct);

    /// <summary>
    ///     Deletes the task instance that was read, checking its row version: a task changed since it was read, such as
    ///     one that gained a manufacture run, is refused with a <c>TaskRunGuard.ChangedPrefix</c> error.
    /// </summary>
    Task<Result> DeleteAsync(Task task, CancellationToken ct);

    /// <summary>Gets all tasks belonging to a specific project.</summary>
    Task<Result<IReadOnlyList<Task>>> GetByProjectIdAsync(Guid projectId, CancellationToken ct);
}
