using CSharpFunctionalExtensions;
using Task = Daedalus.Domain.Entities.Task;

namespace Daedalus.Application.Services;

/// <summary>
///     Orchestrates phase chaining logic: when a task completes, evaluates
///     whether dependent tasks in the same project are now unblocked
///     (all their dependencies satisfied).
/// </summary>
public interface IPhaseOrchestrator
{
    /// <summary>
    ///     Evaluates and logs dependency resolution after a task completes.
    ///     Finds sibling tasks whose dependencies are now fully satisfied,
    ///     making them eligible for the next polling cycle.
    /// </summary>
    /// <param name="completedTask">The task that just completed successfully.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result containing the list of newly unblocked task IDs.</returns>
    Task<Result<IReadOnlyList<string>>> OnTaskCompletedAsync(Task completedTask, CancellationToken ct);
}
