namespace Daedalus.Web.Services;

/// <summary>
///     The project writes. Reads go through <see cref="ApiClient"/>; the writes keep their own wording of a refusal,
///     which the Projects dialogs have always shown.
/// </summary>
public interface IProjectApiClient
{
    /// <summary>Create a new project.</summary>
    Task<Result<ProjectDto>> CreateProjectAsync(CreateProjectDto request, CancellationToken ct = default);

    /// <summary>Update an existing project.</summary>
    Task<Result<ProjectDto>> UpdateProjectAsync(Guid id, UpdateProjectDto request, CancellationToken ct = default);

    /// <summary>Delete a project.</summary>
    Task<Result<bool>> DeleteProjectAsync(Guid id, CancellationToken ct = default);
}
