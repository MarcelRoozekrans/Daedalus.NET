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
    private Task<Result<T>> PostAsync<T>(string url, object body, CancellationToken ct = default)
        where T : class =>
        SendAsync<T>(token => httpClient.PostAsJsonAsync(url, body, token), ct);

    private Task<Result> PostAsync(string url, object body, CancellationToken ct = default) =>
        SendAsync(token => httpClient.PostAsJsonAsync(url, body, token), ct);

    private Task<Result<T>> PutAsync<T>(string url, object body, CancellationToken ct = default)
        where T : class =>
        SendAsync<T>(token => httpClient.PutAsJsonAsync(url, body, token), ct);

    private Task<Result> DeleteAsync(string url, CancellationToken ct = default) =>
        SendAsync(token => httpClient.DeleteAsync(new Uri(url, UriKind.Relative), token), ct);

    /// <summary>
    ///     Sends a write and reads a <typeparamref name="T"/> body on success. Every expected failure is a Result and never
    ///     throws: a refusal carries the server's reason, a success body that is not JSON is a failure, and so is a
    ///     cancelled request.
    /// </summary>
    private static async Task<Result<T>> SendAsync<T>(
        Func<CancellationToken, Task<HttpResponseMessage>> send,
        CancellationToken ct) where T : class
    {
        try
        {
            using var response = await send(ct);
            if (!response.IsSuccessStatusCode)
            {
                return Result<T>.Failure(await ReadFailureReasonAsync(response, ct));
            }

            var value = await TryReadJsonAsync<T>(response, ct);
            return value is not null
                ? Result<T>.Success(value)
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
        catch (OperationCanceledException)
        {
            return Result<T>.Failure("Request was cancelled");
        }
    }

    /// <summary>Sends a write whose success body is not read. Failures map as <see cref="SendAsync{T}"/> maps them.</summary>
    private static async Task<Result> SendAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> send,
        CancellationToken ct)
    {
        try
        {
            using var response = await send(ct);
            return response.IsSuccessStatusCode
                ? Result.Success()
                : Result.Failure(await ReadFailureReasonAsync(response, ct));
        }
        catch (AccessTokenNotAvailableException)
        {
            return Result.Failure("Please log in to perform this action.");
        }
        catch (HttpRequestException ex)
        {
            return Result.Failure($"API error: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            return Result.Failure("Request was cancelled");
        }
    }

    // Task CRUD
    /// <summary>
    ///     Starts a manufacture run for the task. A refusal returns the server's problem detail, such as a repository that
    ///     is not allow-listed, so the page can show why.
    /// </summary>
    public Task<Result<StartWorkflowRunResponse>> ManufactureTaskAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<StartWorkflowRunResponse>(
            token => httpClient.PostAsync(new Uri($"/api/tasks/{id}/manufacture", UriKind.Relative), content: null, token),
            ct);

    private static async Task<T?> TryReadJsonAsync<T>(HttpResponseMessage response, CancellationToken ct) where T : class
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(ct);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    ///     The reason a write was refused. The server answers in two shapes: a ProblemDetails <c>detail</c>, and the
    ///     <c>{ "error": "..." }</c> body most controllers return. A body in neither shape gives the status line.
    /// </summary>
    private static async Task<string> ReadFailureReasonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await TryReadJsonAsync<FailureBody>(response, ct);
        if (!string.IsNullOrWhiteSpace(body?.Detail))
        {
            return body.Detail;
        }

        return !string.IsNullOrWhiteSpace(body?.Error)
            ? body.Error
            : $"API error: {(int)response.StatusCode} {response.ReasonPhrase}";
    }

    private sealed record FailureBody(string? Detail, string? Error);

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
