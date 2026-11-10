using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence.Orm;
using Daedalus.Infrastructure.Persistence.Orm.Tasks;

namespace Daedalus.Api.Services;

/// <summary>Implementation of ITaskExecutionQueryService.</summary>
public sealed class TaskExecutionQueryService(DaedalusDb db) : ITaskExecutionQueryService
{
    public async Task<PagedResultDto<TaskExecutionDto>> GetByTaskIdAsync(Guid taskId, int page = 1, int pageSize = 10,
        CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        var (total, executions) = await new TaskSql(connection)
            .ExecutionsByTaskAsync(taskId, (page - 1) * pageSize, pageSize, ct).ConfigureAwait(false);

        return new PagedResultDto<TaskExecutionDto>(executions.Select(MapToDto).ToList(), total, page, pageSize);
    }

    public async Task<PagedResultDto<TaskExecutionDto>> GetBySessionIdAsync(Guid sessionId, int page = 1,
        int pageSize = 10, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        var (total, executions) = await new TaskSql(connection)
            .ExecutionsBySessionAsync(sessionId, (page - 1) * pageSize, pageSize, ct).ConfigureAwait(false);

        return new PagedResultDto<TaskExecutionDto>(executions.Select(MapToDto).ToList(), total, page, pageSize);
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
