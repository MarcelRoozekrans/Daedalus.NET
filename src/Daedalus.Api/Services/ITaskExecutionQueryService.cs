namespace Daedalus.Api.Services;

/// <summary>Service for accessing task execution data from the database.</summary>
public interface ITaskExecutionQueryService
{
    Task<PagedResultDto<TaskExecutionDto>> GetByTaskIdAsync(Guid taskId, int page = 1, int pageSize = 10,
        CancellationToken ct = default);

    Task<PagedResultDto<TaskExecutionDto>> GetBySessionIdAsync(Guid sessionId, int page = 1, int pageSize = 10,
        CancellationToken ct = default);
}
