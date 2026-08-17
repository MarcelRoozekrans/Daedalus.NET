using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Daedalus.Api.Services;

/// <summary>Implementation of ITaskExecutionQueryService.</summary>
public sealed class TaskExecutionQueryService(ApplicationDbContext dbContext) : ITaskExecutionQueryService
{
    public async Task<PagedResultDto<TaskExecutionDto>> GetByTaskIdAsync(Guid taskId, int page = 1, int pageSize = 10,
        CancellationToken ct = default)
    {
        var total = await dbContext.TaskExecutions.CountAsync(e => e.TaskId == taskId, ct).ConfigureAwait(false);

        var executions = await dbContext.TaskExecutions
            .AsNoTracking()
            .Where(e => e.TaskId == taskId)
            .OrderByDescending(e => e.ExecutedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct).ConfigureAwait(false);

        var dtos = executions.Select(MapToDto).ToList();

        return new PagedResultDto<TaskExecutionDto>(dtos, total, page, pageSize);
    }

    public async Task<PagedResultDto<TaskExecutionDto>> GetBySessionIdAsync(Guid sessionId, int page = 1,
        int pageSize = 10, CancellationToken ct = default)
    {
        var total = await dbContext.TaskExecutions.CountAsync(e => e.SessionId == sessionId, ct).ConfigureAwait(false);

        var executions = await dbContext.TaskExecutions
            .AsNoTracking()
            .Where(e => e.SessionId == sessionId)
            .OrderByDescending(e => e.ExecutedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct).ConfigureAwait(false);

        var dtos = executions.Select(MapToDto).ToList();

        return new PagedResultDto<TaskExecutionDto>(dtos, total, page, pageSize);
    }

    private static TaskExecutionDto MapToDto(TaskExecution execution)
    {
        return new TaskExecutionDto(
            execution.Id,
            execution.TaskId,
            execution.SessionId,
            execution.IterationNumber,
            execution.Prompt,
            execution.LlmResponse,
            execution.CompletionPromiseFound,
            execution.ExecutedAt,
            execution.ExecutionDuration,
            execution.Error,
            execution.InputTokens,
            execution.OutputTokens,
            execution.ModelId
        );
    }
}
