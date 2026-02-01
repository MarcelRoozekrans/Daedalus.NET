using CSharpFunctionalExtensions;

namespace Daedalus.Application.Abstractions;

/// <summary>
///     Persists and retrieves PromptContext state so the loop can recover from crashes.
///     The article's git checkpoint pattern ensures state isn't lost when Ralph fails.
///     Without persistence, a crash during the loop loses all accumulated context.
/// </summary>
public interface IPromptContextStore
{
    /// <summary>
    ///     Persists the current prompt context for crash recovery.
    /// </summary>
    /// <param name="context">The prompt context to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success or failure.</returns>
    Task<Result> SaveAsync(PromptContext context, CancellationToken ct);

    /// <summary>
    ///     Loads a previously saved prompt context for a task and session.
    /// </summary>
    /// <param name="taskId">The task ID.</param>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The prompt context or failure if not found.</returns>
    Task<Result<PromptContext>> LoadAsync(Guid taskId, Guid sessionId, CancellationToken ct);

    /// <summary>
    ///     Deletes a prompt context after task completion (cleanup).
    /// </summary>
    /// <param name="taskId">The task ID.</param>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success or failure.</returns>
    Task<Result> DeleteAsync(Guid taskId, Guid sessionId, CancellationToken ct);
}
