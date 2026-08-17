using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Daedalus.Infrastructure.Persistence.Repositories;

/// <summary>
///     EF Core repository implementation for BrainstormSession.
/// </summary>
public sealed partial class BrainstormRepository(
    ApplicationDbContext dbContext,
    ILogger<BrainstormRepository> logger) : IBrainstormRepository
{
    public async Task<Result<BrainstormSession>> AddAsync(
        BrainstormSession session, CancellationToken ct)
    {
        try
        {
            dbContext.BrainstormSessions.Add(session);
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result.Success(session);
        }
        catch (Exception ex)
        {
            LogErrorAddingSession(logger, ex, session.Id);
            return Result.Failure<BrainstormSession>(
                $"Failed to add brainstorm session: {ex.Message}");
        }
    }

    public async Task<Result<BrainstormSession>> GetByIdAsync(Guid id, CancellationToken ct)
    {
        try
        {
            var session = await dbContext.BrainstormSessions
                .Include(s => s.Messages.OrderBy(m => m.CreatedAt))
                .FirstOrDefaultAsync(s => s.Id == id, ct)
                .ConfigureAwait(false);

            return session is not null
                ? Result.Success(session)
                : Result.Failure<BrainstormSession>(
                    $"Brainstorm session {id} not found.");
        }
        catch (Exception ex)
        {
            LogErrorRetrievingSession(logger, ex, id);
            return Result.Failure<BrainstormSession>(
                $"Error retrieving brainstorm session: {ex.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<BrainstormSession>>> GetByProjectIdAsync(
        Guid projectId, CancellationToken ct)
    {
        try
        {
            var sessions = await dbContext.BrainstormSessions
                .Where(s => s.ProjectId == projectId)
                .OrderByDescending(s => s.CreatedAt)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            return Result.Success<IReadOnlyList<BrainstormSession>>(sessions);
        }
        catch (Exception ex)
        {
            LogErrorRetrievingSessionsByProject(logger, ex, projectId);
            return Result.Failure<IReadOnlyList<BrainstormSession>>(
                $"Error retrieving brainstorm sessions for project: {ex.Message}");
        }
    }

    public async Task<Result> UpdateAsync(BrainstormSession session, CancellationToken ct)
    {
        try
        {
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result.Success();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure(
                "The brainstorm session was modified by another operation. Please retry.");
        }
        catch (Exception ex)
        {
            LogErrorUpdatingSession(logger, ex, session.Id);
            return Result.Failure(
                $"Failed to update brainstorm session: {ex.Message}");
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
