using Daedalus.Application.DTOs;

namespace Daedalus.Application.Abstractions;

/// <summary>Service for querying project data.</summary>
public interface IProjectQueryService
{
    /// <summary>Get all projects with pagination.</summary>
    Task<PagedResultDto<ProjectDto>> GetAllAsync(int page, int pageSize, CancellationToken ct = default);

    /// <summary>Get a specific project by ID.</summary>
    Task<ProjectDto?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Get a project with its tasks.</summary>
    Task<ProjectDto?> GetWithTasksAsync(Guid id, CancellationToken ct = default);
}
