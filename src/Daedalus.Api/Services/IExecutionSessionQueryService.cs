namespace Daedalus.Api.Services;

/// <summary>Service for accessing execution session data from the database.</summary>
public interface IExecutionSessionQueryService
{
    Task<PagedResultDto<ExecutionSessionDto>> GetAllAsync(int page = 1, int pageSize = 10,
        CancellationToken ct = default);

    Task<ExecutionSessionDto?> GetByIdAsync(Guid id, CancellationToken ct = default);

    Task<PagedResultDto<ExecutionSessionDto>> GetActiveAsync(int page = 1, int pageSize = 10,
        CancellationToken ct = default);
}
