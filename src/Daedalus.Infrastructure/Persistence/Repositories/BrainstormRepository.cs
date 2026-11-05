using Daedalus.Application.Abstractions;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence.Orm;
using Daedalus.Infrastructure.Persistence.Orm.Brainstorms;
using Microsoft.Extensions.Logging;
using ZeroAlloc.ORM;
using ZeroAlloc.Results;

namespace Daedalus.Infrastructure.Persistence.Repositories;

/// <summary>
///     Stores brainstorm sessions and their messages on ZeroAlloc.ORM, one pooled connection per call.
/// </summary>
/// <remarks>
///     <see cref="UpdateAsync"/> is guarded by the <c>xmin</c> the session was read with (#290), so a save of a session
///     another write changed since is refused with <see cref="ModifiedByAnotherOperation"/>, and none of its messages is
///     stored. No tracking is needed: a save writes the session's columns and inserts every message whose id is not yet
///     stored. A save does not read the new <c>xmin</c> back into the instance: re-read a session with
///     <see cref="GetByIdAsync"/> after <see cref="AddAsync"/> or <see cref="UpdateAsync"/> before its next guarded save,
///     or that save is refused as modified by another operation (rulings R58, R70, R77).
/// </remarks>
public sealed partial class BrainstormRepository(DaedalusDb db, ILogger<BrainstormRepository> logger)
    : IBrainstormRepository
{
    /// <summary>The failure of a guarded save that lost a race with another write to the session.</summary>
    public const string ModifiedByAnotherOperation =
        "The brainstorm session was modified by another operation. Please retry.";

    public async Task<Result<BrainstormSession>> AddAsync(BrainstormSession session, CancellationToken ct)
    {
        try
        {
            await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
            await new BrainstormSql(connection).InsertAsync(session, ct).ConfigureAwait(false);
            return Result<BrainstormSession>.Success(session);
        }
        catch (Exception ex)
        {
            LogErrorAddingSession(logger, ex, session.Id);
            return Result<BrainstormSession>.Failure(
                $"Failed to add brainstorm session {session.Id}. The cause is logged.");
        }
    }

    public async Task<Result<BrainstormSession>> GetByIdAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
            var session = await new BrainstormSql(connection).FindWithMessagesAsync(id, ct).ConfigureAwait(false);

            return session is not null
                ? Result<BrainstormSession>.Success(session)
                : Result<BrainstormSession>.Failure($"Brainstorm session {id} not found.");
        }
        catch (Exception ex)
        {
            LogErrorRetrievingSession(logger, ex, id);
            return Result<BrainstormSession>.Failure(
                $"Error retrieving brainstorm session {id}. The cause is logged.");
        }
    }

    public async Task<Result<IReadOnlyList<BrainstormSession>>> GetByProjectIdAsync(
        Guid projectId, CancellationToken ct)
    {
        try
        {
            await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
            var sessions = await new BrainstormSql(connection).ListByProjectAsync(projectId, ct).ConfigureAwait(false);
            return Result<IReadOnlyList<BrainstormSession>>.Success(sessions);
        }
        catch (Exception ex)
        {
            LogErrorRetrievingSessionsByProject(logger, ex, projectId);
            return Result<IReadOnlyList<BrainstormSession>>.Failure(
                $"Error retrieving brainstorm sessions for project {projectId}. The cause is logged.");
        }
    }

    /// <summary>
    ///     Writes the session's phase, phase-complete flag, completion time, design document and implementation plan when
    ///     the row still has the <c>xmin</c> the session was read with, and inserts its messages not yet stored, in one
    ///     transaction. A row another write changed since answers <see cref="ModifiedByAnotherOperation"/>; a row deleted
    ///     since answers the logged "Failed to update brainstorm session {id}" (ruling R78). Either way nothing is
    ///     written.
    /// </summary>
    public async Task<Result> UpdateAsync(BrainstormSession session, CancellationToken ct)
    {
        try
        {
            await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
            var sql = new BrainstormSql(connection);
            ZeroAllocOrmConcurrencyException conflict;
            try
            {
                await sql.SaveAsync(session, ct).ConfigureAwait(false);
                return Result.Success();
            }
            catch (ZeroAllocOrmConcurrencyException ex)
            {
                conflict = ex;
            }

            if (await sql.ExistsAsync(session.Id, ct).ConfigureAwait(false))
            {
                return Result.Failure(ModifiedByAnotherOperation);
            }

            LogErrorUpdatingSession(logger, conflict, session.Id);
            return Result.Failure($"Failed to update brainstorm session {session.Id}. The cause is logged.");
        }
        catch (Exception ex)
        {
            LogErrorUpdatingSession(logger, ex, session.Id);
            return Result.Failure($"Failed to update brainstorm session {session.Id}. The cause is logged.");
        }
    }

    [LoggerMessage(EventId = 310, Level = LogLevel.Error,
        Message = "Error adding brainstorm session {SessionId}")]
    private static partial void LogErrorAddingSession(
        ILogger logger, Exception ex, Guid sessionId);

    [LoggerMessage(EventId = 311, Level = LogLevel.Error,
        Message = "Error retrieving brainstorm session {SessionId}")]
    private static partial void LogErrorRetrievingSession(
        ILogger logger, Exception ex, Guid sessionId);

    [LoggerMessage(EventId = 312, Level = LogLevel.Error,
        Message = "Error retrieving brainstorm sessions for project {ProjectId}")]
    private static partial void LogErrorRetrievingSessionsByProject(
        ILogger logger, Exception ex, Guid projectId);

    [LoggerMessage(EventId = 313, Level = LogLevel.Error,
        Message = "Error updating brainstorm session {SessionId}")]
    private static partial void LogErrorUpdatingSession(
        ILogger logger, Exception ex, Guid sessionId);
}
