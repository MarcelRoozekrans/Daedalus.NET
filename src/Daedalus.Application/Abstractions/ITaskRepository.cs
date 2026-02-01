using CSharpFunctionalExtensions;
using Daedalus.Domain.Entities;
using Task = Daedalus.Domain.Entities.Task;

namespace Daedalus.Application.Abstractions;

/// <summary>
///     Repository for task persistence and querying.
/// </summary>
public interface ITaskRepository
{
    /// <summary>Gets a task by ID.</summary>
    Task<Result<Task>> GetByIdAsync(Guid id, CancellationToken ct);

    /// <summary>Gets all pending tasks.</summary>
    Task<Result<IReadOnlyList<Task>>> GetPendingAsync(CancellationToken ct);

    /// <summary>
    ///     Gets pending tasks with database-level pagination.
    ///     Executes Skip/Take at database level for optimal performance.
    /// </summary>
    /// <param name="skip">Number of tasks to skip</param>
    /// <param name="take">Number of tasks to take</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Paginated list of tasks with executions loaded</returns>
    Task<Result<IReadOnlyList<Task>>> GetPendingAsync(int skip, int take, CancellationToken ct);

    /// <summary>
    ///     Gets the count of pending tasks efficiently without loading entities.
    /// </summary>
    Task<Result<int>> GetPendingCountAsync(CancellationToken ct);

    /// <summary>
    ///     Claims the next available pending task atomically (distributed lock).
    /// </summary>
    Task<Result<Task?>> ClaimNextAsync(Guid sessionId, CancellationToken ct);

    /// <summary>Adds a new task.</summary>
    Task<Result<Task>> AddAsync(Task task, CancellationToken ct);

    /// <summary>Updates an existing task.</summary>
    Task<Result> UpdateAsync(Task task, CancellationToken ct);

    /// <summary>Deletes a task by ID.</summary>
    Task<Result> DeleteAsync(Guid id, CancellationToken ct);

    /// <summary>Records a task execution (iteration).</summary>
    Task<Result> RecordExecutionAsync(TaskExecution execution, CancellationToken ct);

    /// <summary>Gets stale tasks that should be marked as abandoned.</summary>
    Task<Result<IReadOnlyList<Task>>> GetStaleInProgressAsync(TimeSpan staleness, CancellationToken ct);

    /// <summary>Gets all tasks belonging to a specific project.</summary>
    Task<Result<IReadOnlyList<Task>>> GetByProjectIdAsync(Guid projectId, CancellationToken ct);
}
