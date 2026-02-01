using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Daedalus.Infrastructure.Persistence;

/// <summary>
///     Execution session repository for session lifecycle management.
/// </summary>
public sealed partial class ExecutionSessionRepository(
    ApplicationDbContext dbContext,
    ILogger<ExecutionSessionRepository> logger) : IExecutionSessionRepository
{
    public async Task<Result<ExecutionSession>> GetByIdAsync(Guid id, CancellationToken ct)
    {
        try
        {
            var session = await dbContext.ExecutionSessions
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == id, ct).ConfigureAwait(false);

            return session is not null
                ? Result.Success(session)
                : Result.Failure<ExecutionSession>($"Session {id} not found");
        }
        catch (Exception ex)
        {
            LogErrorRetrievingSession(logger, ex, id);
            return Result.Failure<ExecutionSession>($"Error retrieving session: {ex.Message}");
        }
    }

    public async Task<Result<ExecutionSession>> AddAsync(ExecutionSession session, CancellationToken ct)
    {
        try
        {
            dbContext.ExecutionSessions.Add(session);
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            LogSessionCreated(logger, session.Id, session.WorkerName);
            return Result.Success(session);
        }
        catch (Exception ex)
        {
            LogErrorAddingSession(logger, ex, session.Id);
            return Result.Failure<ExecutionSession>($"Error adding session: {ex.Message}");
        }
    }

    public async Task<Result> UpdateAsync(ExecutionSession session, CancellationToken ct)
    {
        try
        {
            dbContext.ExecutionSessions.Update(session);
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result.Success();
        }
        catch (Exception ex)
        {
            LogErrorUpdatingSession(logger, ex, session.Id);
            return Result.Failure($"Error updating session: {ex.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<ExecutionSession>>> GetActiveAsync(CancellationToken ct)
    {
        try
        {
            var sessions = await dbContext.ExecutionSessions
                .AsNoTracking()
                .Where(s => s.IsActive)
                .OrderBy(s => s.StartedAt)
                .ToListAsync(ct).ConfigureAwait(false);

            return Result.Success((IReadOnlyList<ExecutionSession>)sessions);
        }
        catch (Exception ex)
        {
            LogErrorRetrievingActiveSessions(logger, ex);
            return Result.Failure<IReadOnlyList<ExecutionSession>>($"Error retrieving sessions: {ex.Message}");
        }
    }

    [LoggerMessage(EventId = 20, Level = LogLevel.Error, Message = "Error retrieving session {SessionId}")]
    private static partial void LogErrorRetrievingSession(ILogger logger, Exception exception, Guid sessionId);

    [LoggerMessage(EventId = 21, Level = LogLevel.Information, Message = "Session {SessionId} ({WorkerName}) created")]
    private static partial void LogSessionCreated(ILogger logger, Guid sessionId, string workerName);

    [LoggerMessage(EventId = 22, Level = LogLevel.Error, Message = "Error adding session {SessionId}")]
    private static partial void LogErrorAddingSession(ILogger logger, Exception exception, Guid sessionId);

    [LoggerMessage(EventId = 23, Level = LogLevel.Error, Message = "Error updating session {SessionId}")]
    private static partial void LogErrorUpdatingSession(ILogger logger, Exception exception, Guid sessionId);

    [LoggerMessage(EventId = 24, Level = LogLevel.Error, Message = "Error retrieving active sessions")]
    private static partial void LogErrorRetrievingActiveSessions(ILogger logger, Exception exception);
}
