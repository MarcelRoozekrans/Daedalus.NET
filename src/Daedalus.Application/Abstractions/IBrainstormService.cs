using CSharpFunctionalExtensions;
using Daedalus.Domain.Entities;

namespace Daedalus.Application.Abstractions;

/// <summary>
///     Service for managing interactive brainstorming sessions.
///     Coordinates between the user, LLM agent, and repository to guide
///     structured brainstorming through multiple phases.
/// </summary>
public interface IBrainstormService
{
    /// <summary>
    ///     Creates a new brainstorm session for a project, gathering initial context
    ///     from the project metadata and invoking the LLM to produce a context summary.
    /// </summary>
    /// <param name="projectId">The project to brainstorm about.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created session with the initial assistant message, or failure.</returns>
    Task<Result<BrainstormSession>> CreateSessionAsync(Guid projectId, CancellationToken ct);

    /// <summary>
    ///     Gets a brainstorm session by ID, including all messages.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The session, or failure if not found.</returns>
    Task<Result<BrainstormSession>> GetSessionAsync(Guid sessionId, CancellationToken ct);

    /// <summary>
    ///     Gets all brainstorm sessions for a project, ordered by newest first.
    /// </summary>
    /// <param name="projectId">The project ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The list of sessions, or failure.</returns>
    Task<Result<IReadOnlyList<BrainstormSession>>> GetSessionsByProjectAsync(Guid projectId, CancellationToken ct);

    /// <summary>
    ///     Sends a user message to a brainstorm session, invokes the LLM to generate
    ///     a response, and persists both messages.
    /// </summary>
    /// <param name="sessionId">The session to send the message to.</param>
    /// <param name="userMessage">The user's message text.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The assistant's response message, or failure.</returns>
    Task<Result<BrainstormMessage>> SendMessageAsync(Guid sessionId, string userMessage, CancellationToken ct);

    /// <summary>
    ///     Advances the session to the next brainstorming phase. Requires that
    ///     the LLM has signaled phase completion via the [PHASE_COMPLETE] marker.
    /// </summary>
    /// <param name="sessionId">The session to advance.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated session in its new phase, or failure.</returns>
    Task<Result<BrainstormSession>> AdvancePhaseAsync(Guid sessionId, CancellationToken ct);

    /// <summary>
    ///     Abandons a brainstorm session, setting it to the terminal Abandoned state.
    /// </summary>
    /// <param name="sessionId">The session to abandon.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success or failure.</returns>
    Task<Result> AbandonSessionAsync(Guid sessionId, CancellationToken ct);
}
