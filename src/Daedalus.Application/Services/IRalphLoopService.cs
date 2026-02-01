using CSharpFunctionalExtensions;
using Task = Daedalus.Domain.Entities.Task;

namespace Daedalus.Application.Services;

/// <summary>
///     Orchestrates the Ralph loop execution for a single task.
/// </summary>
public interface IRalphLoopService
{
    /// <summary>
    ///     Executes a Ralph loop task until completion or max iterations.
    /// </summary>
    Task<Result> ExecuteAsync(Task task, Guid sessionId, CancellationToken ct);

    /// <summary>
    ///     Extracts learnings from task execution history and persists them for future iterations.
    /// </summary>
    Task<Result> ExtractAndPersistLearningsAsync(Task task, CancellationToken ct);
}
