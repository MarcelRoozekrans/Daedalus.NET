namespace Daedalus.Web.Services;

/// <summary>Service for accessing projects from the API.</summary>
public interface IProjectApiClient
{
    /// <summary>Get all projects with pagination.</summary>
    Task<Result<PagedResultDto<ProjectDto>>> GetAllProjectsAsync(int page = 1, int pageSize = 10,
        CancellationToken ct = default);

    /// <summary>Get a specific project by ID.</summary>
    Task<Result<ProjectDto>> GetProjectByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Get a project with its tasks.</summary>
    Task<Result<ProjectDto>> GetProjectWithTasksAsync(Guid id, CancellationToken ct = default);

    /// <summary>Create a new project.</summary>
    Task<Result<ProjectDto>> CreateProjectAsync(CreateProjectDto request, CancellationToken ct = default);

    /// <summary>Update an existing project.</summary>
    Task<Result<ProjectDto>> UpdateProjectAsync(Guid id, UpdateProjectDto request, CancellationToken ct = default);

    /// <summary>Delete a project.</summary>
    Task<Result<bool>> DeleteProjectAsync(Guid id, CancellationToken ct = default);
}
