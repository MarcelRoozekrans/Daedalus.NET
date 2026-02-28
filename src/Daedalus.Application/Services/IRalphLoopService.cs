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
    /// <param name="task">The task to execute.</param>
    /// <param name="sessionId">The worker session ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="workspacePath">Optional per-task workspace path for git operations.</param>
    Task<Result> ExecuteAsync(Task task, Guid sessionId, CancellationToken ct, string? workspacePath = null);

    /// <summary>
    ///     Extracts learnings from task execution history and persists them for future iterations.
    /// </summary>
    Task<Result> ExtractAndPersistLearningsAsync(Task task, CancellationToken ct);
}
