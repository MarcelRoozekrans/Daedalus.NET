global using ZeroAlloc.Results;
using Daedalus.Application.DTOs.Scheduling;
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
                ? Result<T>.Success(result)
                : Result<T>.Failure("No data returned from server");
        }
        catch (AccessTokenNotAvailableException)
        {
            return Result<T>.Failure("Please log in to access this data.");
        }
        catch (HttpRequestException ex)
        {
            return Result<T>.Failure($"API error: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            return Result<T>.Failure("Request was cancelled");
        }
        catch (Exception ex)
        {
            var message = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
            return Result<T>.Failure($"Unexpected error: {message}");
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

    // Schedules
    public async Task<Result<List<RunDiagnosis>>> GetScheduleOverviewAsync(CancellationToken ct = default) =>
        await GetAsync<List<RunDiagnosis>>("/api/schedules", ct);

    public async Task<Result<List<RunDiagnosis>>> GetScheduleRunHistoryAsync(
        Guid scheduleId,
        int take = 20,
        CancellationToken ct = default) =>
        await GetAsync<List<RunDiagnosis>>($"/api/schedules/{scheduleId}/runs?take={take}", ct);

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
                ? Result<T>.Success(result)
                : Result<T>.Failure("No data returned from server");
        }
        catch (AccessTokenNotAvailableException)
        {
            return Result<T>.Failure("Please log in to perform this action.");
        }
        catch (HttpRequestException ex)
        {
            return Result<T>.Failure($"API error: {ex.Message}");
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
                ? Result<T>.Success(result)
                : Result<T>.Failure("No data returned from server");
        }
        catch (AccessTokenNotAvailableException)
        {
            return Result<T>.Failure("Please log in to perform this action.");
        }
        catch (HttpRequestException ex)
        {
            return Result<T>.Failure($"API error: {ex.Message}");
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
    /// <summary>
    ///     Starts a manufacture run for the task. A refusal returns the server's problem detail, such as a repository that
    ///     is not allow-listed, so the page can show why.
    /// </summary>
    public async Task<Result<StartWorkflowRunResponse>> ManufactureTaskAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            using var response = await httpClient.PostAsync(new Uri($"/api/tasks/{id}/manufacture", UriKind.Relative), content: null, ct);
            if (response.IsSuccessStatusCode)
            {
                var started = await ReadStartedAsync(response, ct);
                return started is not null
                    ? Result<StartWorkflowRunResponse>.Success(started)
                    : Result<StartWorkflowRunResponse>.Failure("No data returned from server");
            }

            var detail = await ReadProblemDetailAsync(response, ct);
            return Result<StartWorkflowRunResponse>.Failure(detail ?? $"API error: {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (AccessTokenNotAvailableException)
        {
            return Result<StartWorkflowRunResponse>.Failure("Please log in to perform this action.");
        }
        catch (HttpRequestException ex)
        {
            return Result<StartWorkflowRunResponse>.Failure($"API error: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            return Result<StartWorkflowRunResponse>.Failure("Request was cancelled");
        }
    }

    private static async Task<StartWorkflowRunResponse?> ReadStartedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<StartWorkflowRunResponse>(ct);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task<string?> ReadProblemDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(ct);
            return string.IsNullOrWhiteSpace(problem?.Detail) ? null : problem.Detail;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    private sealed record ProblemBody(string? Detail);

    public async Task<Result<TaskDto>> CreateTaskAsync(CreateTaskDto dto, CancellationToken ct = default) =>
        await PostAsync<TaskDto>("/api/tasks", dto, ct);

    public async Task<Result<TaskDto>> UpdateTaskAsync(Guid id, UpdateTaskDto dto, CancellationToken ct = default) =>
        await PutAsync<TaskDto>($"/api/tasks/{id}", dto, ct);

    public async Task<Result> DeleteTaskAsync(Guid id, CancellationToken ct = default) =>
        await DeleteAsync($"/api/tasks/{id}", ct);

    // Cost Analytics
    public async Task<Result<CostSummaryDto>> GetCostSummaryAsync(CancellationToken ct = default) =>
        await GetAsync<CostSummaryDto>("/api/cost-analytics/summary", ct);

    public async Task<Result<List<ProjectCostDto>>> GetCostsByProjectAsync(CancellationToken ct = default) =>
        await GetAsync<List<ProjectCostDto>>("/api/cost-analytics/by-project", ct);

    public async Task<Result<List<TaskCostDto>>> GetCostsByProjectIdAsync(Guid projectId, CancellationToken ct = default) =>
        await GetAsync<List<TaskCostDto>>($"/api/cost-analytics/by-project/{projectId}", ct);

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

    // Schedules — resend
    /// <summary>
    ///     Requeues an undelivered run's message for redelivery.
    /// </summary>
    public async Task<Result> ResendScheduleRunAsync(
        Guid scheduleId, Guid executionId, CancellationToken ct = default) =>
        await PostAsync($"/api/schedules/{scheduleId}/runs/{executionId}/resend", new { }, ct);
}
