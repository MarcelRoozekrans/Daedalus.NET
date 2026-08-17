global using CSharpFunctionalExtensions;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using BrainstormMessageDto = Daedalus.Application.DTOs.BrainstormMessageDto;
using BrainstormSessionDto = Daedalus.Application.DTOs.BrainstormSessionDto;
using BrainstormSessionSummaryDto = Daedalus.Application.DTOs.BrainstormSessionSummaryDto;
using CreateBrainstormSessionDto = Daedalus.Application.DTOs.CreateBrainstormSessionDto;
using SendBrainstormMessageDto = Daedalus.Application.DTOs.SendBrainstormMessageDto;

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
        catch (AccessTokenNotAvailableException)
        {
            return Result.Failure<T>("Please log in to access this data.");
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
            var message = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
            return Result.Failure<T>($"Unexpected error: {message}");
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
        catch (AccessTokenNotAvailableException)
        {
            return Result.Failure<T>("Please log in to perform this action.");
        }
        catch (HttpRequestException ex)
        {
            return Result.Failure<T>($"API error: {ex.Message}");
        }
    }

    private async Task<Result> PostAsync(string url, object body, CancellationToken ct = default)
    {
        try
        {
            var response = await httpClient.PostAsJsonAsync(url, body, ct);
            response.EnsureSuccessStatusCode();
            return Result.Success();
        }
        catch (AccessTokenNotAvailableException)
        {
            return Result.Failure("Please log in to perform this action.");
        }
        catch (HttpRequestException ex)
        {
            return Result.Failure($"API error: {ex.Message}");
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
        catch (AccessTokenNotAvailableException)
        {
            return Result.Failure<T>("Please log in to perform this action.");
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
        catch (AccessTokenNotAvailableException)
        {
            return Result.Failure("Please log in to perform this action.");
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

    // Cost Analytics
    public async Task<Result<CostSummaryDto>> GetCostSummaryAsync(CancellationToken ct = default) =>
        await GetAsync<CostSummaryDto>("/api/cost-analytics/summary", ct);

    public async Task<Result<List<ProjectCostDto>>> GetCostsByProjectAsync(CancellationToken ct = default) =>
        await GetAsync<List<ProjectCostDto>>("/api/cost-analytics/by-project", ct);

    public async Task<Result<List<TaskCostDto>>> GetCostsByProjectIdAsync(Guid projectId, CancellationToken ct = default) =>
        await GetAsync<List<TaskCostDto>>($"/api/cost-analytics/by-project/{projectId}", ct);

    public async Task<Result<List<TaskCostDto>>> GetCostsBySessionIdAsync(Guid sessionId, CancellationToken ct = default) =>
        await GetAsync<List<TaskCostDto>>($"/api/cost-analytics/by-session/{sessionId}", ct);

    public async Task<Result<CostEstimateDto>> EstimateCostAsync(
        string modelId, int maxIterations = 10, int estimatedPromptTokens = 4000,
        CancellationToken ct = default) =>
        await GetAsync<CostEstimateDto>(
            $"/api/cost-analytics/estimate?modelId={Uri.EscapeDataString(modelId)}&maxIterations={maxIterations}&estimatedPromptTokens={estimatedPromptTokens}",
            ct);

    public async Task<Result<List<ModelPricingDto>>> GetModelPricingAsync(CancellationToken ct = default) =>
        await GetAsync<List<ModelPricingDto>>("/api/cost-analytics/pricing", ct);

    // Brainstorm Sessions
    public async Task<Result<BrainstormSessionDto>> CreateBrainstormSessionAsync(
        CreateBrainstormSessionDto dto, CancellationToken ct = default) =>
        await PostAsync<BrainstormSessionDto>("/api/brainstorm/sessions", dto, ct);

    public async Task<Result<BrainstormSessionDto>> GetBrainstormSessionAsync(
        Guid sessionId, CancellationToken ct = default) =>
        await GetAsync<BrainstormSessionDto>($"/api/brainstorm/sessions/{sessionId}", ct);

    public async Task<Result<List<BrainstormSessionSummaryDto>>> GetBrainstormSessionsAsync(
        Guid projectId, CancellationToken ct = default) =>
        await GetAsync<List<BrainstormSessionSummaryDto>>($"/api/brainstorm/sessions?projectId={projectId}", ct);

    public async Task<Result<BrainstormMessageDto>> SendBrainstormMessageAsync(
        Guid sessionId, SendBrainstormMessageDto dto, CancellationToken ct = default) =>
        await PostAsync<BrainstormMessageDto>($"/api/brainstorm/sessions/{sessionId}/messages", dto, ct);

    public async Task<Result<BrainstormSessionDto>> AdvanceBrainstormPhaseAsync(
        Guid sessionId, CancellationToken ct = default) =>
        await PostAsync<BrainstormSessionDto>($"/api/brainstorm/sessions/{sessionId}/advance", new { }, ct);

    public async Task<Result> AbandonBrainstormSessionAsync(
        Guid sessionId, CancellationToken ct = default) =>
        await PostAsync($"/api/brainstorm/sessions/{sessionId}/abandon", new { }, ct);

    public async Task<Result<List<TaskDto>>> GenerateBrainstormTasksAsync(
        Guid sessionId, CancellationToken ct = default) =>
        await PostAsync<List<TaskDto>>($"/api/brainstorm/sessions/{sessionId}/generate-tasks", new { }, ct);
}
