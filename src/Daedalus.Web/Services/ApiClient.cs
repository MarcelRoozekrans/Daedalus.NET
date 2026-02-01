global using CSharpFunctionalExtensions;

namespace Daedalus.Web.Services;

// DTOs (copied from API for client-side use)

/// <summary>HTTP client service for communicating with the API.</summary>
public sealed class ApiClient(HttpClient httpClient)
{
    /// <summary>
    ///     Executes an HTTP GET request with Result of T error handling and cancellation support.
    /// </summary>
    private async Task<Result<T>> GetAsync<T>(
        string url,
        CancellationToken ct = default) where T : class
    {
        try
        {
            var result = await httpClient.GetFromJsonAsync<T>(url, ct);
            return result is not null
                ? Result.Success(result)
                : Result.Failure<T>("No data returned from server");
        }
        catch (HttpRequestException ex)
        {
            return Result.Failure<T>($"API error: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            return Result.Failure<T>("Request was cancelled");
        }
        catch (Exception ex)
        {
            return Result.Failure<T>($"Unexpected error: {ex.Message}");
        }
    }

    // Tasks
    public async Task<Result<PagedResultDto<TaskDto>>> GetTasksAsync(
        int page = 1,
        int pageSize = 10,
        CancellationToken ct = default) =>
        await GetAsync<PagedResultDto<TaskDto>>(
            $"/api/tasks?page={page}&pageSize={pageSize}",
            ct);

    public async Task<Result<TaskDto>> GetTaskAsync(
        Guid id,
        CancellationToken ct = default) =>
        await GetAsync<TaskDto>($"/api/tasks/{id}", ct);

    // ExecutionSessions
    public async Task<Result<PagedResultDto<ExecutionSessionDto>>> GetSessionsAsync(
        int page = 1,
        int pageSize = 10,
        CancellationToken ct = default) =>
        await GetAsync<PagedResultDto<ExecutionSessionDto>>(
            $"/api/executionsessions?page={page}&pageSize={pageSize}",
            ct);

    public async Task<Result<PagedResultDto<ExecutionSessionDto>>> GetActiveSessionsAsync(
        int page = 1,
        int pageSize = 10,
        CancellationToken ct = default) =>
        await GetAsync<PagedResultDto<ExecutionSessionDto>>(
            $"/api/executionsessions/active?page={page}&pageSize={pageSize}",
            ct);

    public async Task<Result<ExecutionSessionDto>> GetSessionAsync(
        Guid id,
        CancellationToken ct = default) =>
        await GetAsync<ExecutionSessionDto>($"/api/executionsessions/{id}", ct);

    // TaskExecutions
    public async Task<Result<PagedResultDto<TaskExecutionDto>>> GetExecutionsByTaskAsync(
        Guid taskId,
        int page = 1,
        int pageSize = 10,
        CancellationToken ct = default) =>
        await GetAsync<PagedResultDto<TaskExecutionDto>>(
            $"/api/taskexecutions/task/{taskId}?page={page}&pageSize={pageSize}",
            ct);

    public async Task<Result<PagedResultDto<TaskExecutionDto>>> GetExecutionsBySessionAsync(
        Guid sessionId,
        int page = 1,
        int pageSize = 10,
        CancellationToken ct = default) =>
        await GetAsync<PagedResultDto<TaskExecutionDto>>(
            $"/api/taskexecutions/session/{sessionId}?page={page}&pageSize={pageSize}",
            ct);

    // Projects
    public async Task<Result<PagedResultDto<ProjectDto>>> GetProjectsAsync(
        int page = 1,
        int pageSize = 10,
        CancellationToken ct = default) =>
        await GetAsync<PagedResultDto<ProjectDto>>(
            $"/api/projects?page={page}&pageSize={pageSize}",
            ct);

    public async Task<Result<ProjectDto>> GetProjectAsync(Guid id, CancellationToken ct = default) =>
        await GetAsync<ProjectDto>($"/api/projects/{id}", ct);

    public async Task<Result<ProjectDto>> GetProjectWithTasksAsync(Guid id, CancellationToken ct = default) =>
        await GetAsync<ProjectDto>($"/api/projects/{id}/with-tasks", ct);

    // Write helpers
    private async Task<Result<T>> PostAsync<T>(string url, object body, CancellationToken ct = default)
        where T : class
    {
        try
        {
            var response = await httpClient.PostAsJsonAsync(url, body, ct);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<T>(ct);
            return result is not null
                ? Result.Success(result)
                : Result.Failure<T>("No data returned from server");
        }
        catch (HttpRequestException ex)
        {
            return Result.Failure<T>($"API error: {ex.Message}");
        }
    }

    private async Task<Result<T>> PutAsync<T>(string url, object body, CancellationToken ct = default)
        where T : class
    {
        try
        {
            var response = await httpClient.PutAsJsonAsync(url, body, ct);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<T>(ct);
            return result is not null
                ? Result.Success(result)
                : Result.Failure<T>("No data returned from server");
        }
        catch (HttpRequestException ex)
        {
            return Result.Failure<T>($"API error: {ex.Message}");
        }
    }

    private async Task<Result> DeleteAsync(string url, CancellationToken ct = default)
    {
        try
        {
            var response = await httpClient.DeleteAsync(new Uri(url, UriKind.Relative), ct);
            response.EnsureSuccessStatusCode();
            return Result.Success();
        }
        catch (HttpRequestException ex)
        {
            return Result.Failure($"API error: {ex.Message}");
        }
    }

    // Task CRUD
    public async Task<Result<TaskDto>> CreateTaskAsync(CreateTaskDto dto, CancellationToken ct = default) =>
        await PostAsync<TaskDto>("/api/tasks", dto, ct);

    public async Task<Result<TaskDto>> UpdateTaskAsync(Guid id, UpdateTaskDto dto, CancellationToken ct = default) =>
        await PutAsync<TaskDto>($"/api/tasks/{id}", dto, ct);

    public async Task<Result> DeleteTaskAsync(Guid id, CancellationToken ct = default) =>
        await DeleteAsync($"/api/tasks/{id}", ct);

    public async Task<Result<TaskDto>> AbandonTaskAsync(Guid id, AbandonTaskDto dto, CancellationToken ct = default) =>
        await PostAsync<TaskDto>($"/api/tasks/{id}/abandon", dto, ct);

    public async Task<Result<TaskDto>> ResumeTaskAsync(Guid id, ResumeTaskDto dto, CancellationToken ct = default) =>
        await PostAsync<TaskDto>($"/api/tasks/{id}/resume", dto, ct);

    // Ralph Config
    public async Task<Result<RalphConfigDto>> GetRalphConfigAsync(CancellationToken ct = default) =>
        await GetAsync<RalphConfigDto>("/api/ralph-config", ct);

    public async Task<Result<RalphConfigDto>>
        UpdateRalphConfigAsync(RalphConfigDto dto, CancellationToken ct = default) =>
        await PutAsync<RalphConfigDto>("/api/ralph-config", dto, ct);
}
