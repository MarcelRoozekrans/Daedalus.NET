using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Daedalus.Api.Services;

/// <summary>Implementation of IExecutionSessionQueryService.</summary>
public sealed class ExecutionSessionQueryService(ApplicationDbContext dbContext) : IExecutionSessionQueryService
{
    public async Task<PagedResultDto<ExecutionSessionDto>> GetAllAsync(int page = 1, int pageSize = 10,
        CancellationToken ct = default)
    {
        var total = await dbContext.ExecutionSessions.CountAsync(ct).ConfigureAwait(false);

        var sessions = await dbContext.ExecutionSessions
            .AsNoTracking()
            .OrderByDescending(s => s.StartedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct).ConfigureAwait(false);

        var dtos = sessions.Select(MapToDto).ToList();

        return new PagedResultDto<ExecutionSessionDto>(dtos, total, page, pageSize);
    }

    public async Task<ExecutionSessionDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var session = await dbContext.ExecutionSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id, ct).ConfigureAwait(false);

        return session is null ? null : MapToDto(session);
    }

    public async Task<PagedResultDto<ExecutionSessionDto>> GetActiveAsync(int page = 1, int pageSize = 10,
        CancellationToken ct = default)
    {
        var total = await dbContext.ExecutionSessions.CountAsync(s => s.IsActive, ct).ConfigureAwait(false);

        var sessions = await dbContext.ExecutionSessions
            .AsNoTracking()
            .Where(s => s.IsActive)
            .OrderByDescending(s => s.LastHeartbeat)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct).ConfigureAwait(false);

        var dtos = sessions.Select(MapToDto).ToList();

        return new PagedResultDto<ExecutionSessionDto>(dtos, total, page, pageSize);
    }

    private static ExecutionSessionDto MapToDto(ExecutionSession session)
    {
        return new ExecutionSessionDto(
            session.Id,
            session.WorkerName,
            session.StartedAt,
            session.LastHeartbeat,
            session.IsActive,
            session.TasksCompleted
        );
    }
}
