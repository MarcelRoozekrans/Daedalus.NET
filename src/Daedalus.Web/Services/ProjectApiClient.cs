using System.Text.Json;

namespace Daedalus.Web.Services;

/// <summary>Implementation of project API client.</summary>
public sealed class ProjectApiClient(ApiClient apiClient, HttpClient httpClient) : IProjectApiClient
{
    public async Task<Result<PagedResultDto<ProjectDto>>> GetAllProjectsAsync(int page = 1, int pageSize = 10,
        CancellationToken ct = default) =>
        await apiClient.GetProjectsAsync(page, pageSize, ct).ConfigureAwait(false);

    public async Task<Result<ProjectDto>> GetProjectByIdAsync(Guid id, CancellationToken ct = default) =>
        await apiClient.GetProjectAsync(id, ct).ConfigureAwait(false);

    public async Task<Result<ProjectDto>> GetProjectWithTasksAsync(Guid id, CancellationToken ct = default) =>
        await apiClient.GetProjectWithTasksAsync(id, ct).ConfigureAwait(false);

    public async Task<Result<ProjectDto>> CreateProjectAsync(CreateProjectDto request, CancellationToken ct = default)
    {
        try
        {
            var response = await httpClient.PostAsJsonAsync("/api/projects", request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return Result.Failure<ProjectDto>($"Failed to create project: {error}");
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var result = JsonSerializer.Deserialize<ProjectDto>(json);
            return result is not null
                ? Result.Success(result)
                : Result.Failure<ProjectDto>("Failed to deserialize response");
        }
        catch (Exception ex)
        {
            return Result.Failure<ProjectDto>($"Error creating project: {ex.Message}");
        }
    }

    public async Task<Result<ProjectDto>> UpdateProjectAsync(Guid id, UpdateProjectDto request,
        CancellationToken ct = default)
    {
        try
        {
            var response = await httpClient.PutAsJsonAsync($"/api/projects/{id}", request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return Result.Failure<ProjectDto>($"Failed to update project: {error}");
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var result = JsonSerializer.Deserialize<ProjectDto>(json);
            return result is not null
                ? Result.Success(result)
                : Result.Failure<ProjectDto>("Failed to deserialize response");
        }
        catch (Exception ex)
        {
            return Result.Failure<ProjectDto>($"Error updating project: {ex.Message}");
        }
    }

    public async Task<Result<bool>> DeleteProjectAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var uri = new Uri($"/api/projects/{id}", UriKind.Relative);
            var response = await httpClient.DeleteAsync(uri, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return Result.Failure<bool>($"Failed to delete project: {error}");
            }

            return Result.Success(true);
        }
        catch (Exception ex)
        {
            return Result.Failure<bool>($"Error deleting project: {ex.Message}");
        }
    }
}
