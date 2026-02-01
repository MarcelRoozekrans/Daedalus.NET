using CSharpFunctionalExtensions;
using Task = Daedalus.Domain.Entities.Task;

namespace Daedalus.Application.Services;

/// <summary>
///     Manages distributed task assignment with session awareness.
/// </summary>
public interface ITaskAssignmentService
{
    /// <summary>
    ///     Gets the next available task for a session, with distributed locking.
    /// </summary>
    Task<Result<Task?>> GetNextAvailableTaskAsync(Guid sessionId, CancellationToken ct);

    /// <summary>
    ///     Detects and reclaims tasks from stale sessions.
    /// </summary>
    Task<Result> ReclaimStaleTasksAsync(CancellationToken ct);
}
