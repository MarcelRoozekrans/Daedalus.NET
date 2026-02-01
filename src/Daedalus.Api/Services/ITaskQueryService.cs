namespace Daedalus.Api.Services;

/// <summary>Service for accessing task data from the database.</summary>
public interface ITaskQueryService
{
    Task<PagedResultDto<TaskDto>> GetAllAsync(int page = 1, int pageSize = 10, CancellationToken ct = default);
    Task<TaskDto?> GetByIdAsync(Guid id, CancellationToken ct = default);
}
