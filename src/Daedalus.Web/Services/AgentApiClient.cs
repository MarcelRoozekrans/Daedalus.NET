using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Daedalus.Application.DTOs;
using Daedalus.Application.DTOs.Agents;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace Daedalus.Web.Services;

/// <summary>
///     Client for <c>/api/agents</c>. Non-streaming calls follow the <see cref="ApiClient"/> convention (CSFE <see cref="Result"/>,
///     ProblemDetails <c>detail</c> as the error text). Streaming turns use fetch + a hand-rolled SSE parser
///     (<see cref="SseReader"/>) because the browser <c>EventSource</c> can neither POST nor send bearer tokens.
/// </summary>
public sealed class AgentApiClient(HttpClient http)
{
    private const string ErrorEventKind = "error";
    private const string LoginRequired = "Please log in to access the agent.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>All registered agents.</summary>
    public Task<Result<IReadOnlyList<AgentSummaryDto>>> GetAgentsAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<AgentSummaryDto>>("/api/agents", ct);

    /// <summary>Creates a session for <paramref name="agentId"/> and returns the new session id.</summary>
    public async Task<Result<string>> CreateSessionAsync(string agentId, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PostAsync(Relative($"/api/agents/{Uri.EscapeDataString(agentId)}/sessions"), content: null, ct);
            if (!response.IsSuccessStatusCode)
            {
                return Result.Failure<string>(await ReadProblemMessageAsync(response, ct));
            }

            var created = await response.Content.ReadFromJsonAsync<CreateAgentSessionResponseDto>(Json, ct);
            return created is null
                ? Result.Failure<string>("No data returned from server")
                : Result.Success(created.SessionId);
        }
        catch (AccessTokenNotAvailableException)
        {
            return Result.Failure<string>(LoginRequired);
        }
        catch (HttpRequestException ex)
        {
            return Result.Failure<string>($"API error: {ex.Message}");
        }
    }

    /// <summary>The caller's sessions, newest first.</summary>
    public Task<Result<IReadOnlyList<AgentSessionDto>>> ListSessionsAsync(int skip = 0, int take = 20, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<AgentSessionDto>>($"/api/agents/sessions?skip={skip}&take={take}", ct);

    /// <summary>Session header plus transcript.</summary>
    public Task<Result<AgentSessionDetailDto>> GetSessionAsync(string sessionId, CancellationToken ct = default) =>
        GetAsync<AgentSessionDetailDto>($"/api/agents/sessions/{Uri.EscapeDataString(sessionId)}", ct);

    /// <summary>Closes (terminates) a session.</summary>
    public async Task<Result> CloseSessionAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.DeleteAsync(Relative($"/api/agents/sessions/{Uri.EscapeDataString(sessionId)}"), ct);
            return response.IsSuccessStatusCode
                ? Result.Success()
                : Result.Failure(await ReadProblemMessageAsync(response, ct));
        }
        catch (AccessTokenNotAvailableException)
        {
            return Result.Failure(LoginRequired);
        }
        catch (HttpRequestException ex)
        {
            return Result.Failure($"API error: {ex.Message}");
        }
    }

    /// <summary>The caller's own memories plus the shared owner's, most recently updated first.</summary>
    public Task<Result<PagedResultDto<MemoryDto>>> ListMemoriesAsync(
        string? kind = null,
        string? agentId = null,
        bool includeArchived = false,
        int page = 1,
        int pageSize = 20,
        CancellationToken ct = default)
    {
        var query = new List<string> { $"page={page}", $"pageSize={pageSize}", $"includeArchived={(includeArchived ? "true" : "false")}" };
        if (!string.IsNullOrEmpty(kind))
        {
            query.Add($"kind={Uri.EscapeDataString(kind)}");
        }

        if (!string.IsNullOrEmpty(agentId))
        {
            query.Add($"agentId={Uri.EscapeDataString(agentId)}");
        }

        return GetAsync<PagedResultDto<MemoryDto>>($"/api/agent-memories?{string.Join('&', query)}", ct);
    }

    /// <summary>One memory the caller can see (their own or the shared owner's).</summary>
    public Task<Result<MemoryDto>> GetMemoryAsync(string id, CancellationToken ct = default) =>
        GetAsync<MemoryDto>($"/api/agent-memories/{Uri.EscapeDataString(id)}", ct);

    /// <summary>Forgets a memory: archives it, or deletes it when <paramref name="hard"/> is set.</summary>
    public async Task<Result> ForgetMemoryAsync(string id, bool hard = false, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.DeleteAsync(Relative($"/api/agent-memories/{Uri.EscapeDataString(id)}?hard={(hard ? "true" : "false")}"), ct);
            return response.IsSuccessStatusCode
                ? Result.Success()
                : Result.Failure(await ReadProblemMessageAsync(response, ct));
        }
        catch (AccessTokenNotAvailableException)
        {
            return Result.Failure(LoginRequired);
        }
        catch (HttpRequestException ex)
        {
            return Result.Failure($"API error: {ex.Message}");
        }
    }

    /// <summary>
    ///     Runs one turn and streams its events. A non-2xx response (e.g. 409 SessionBusy before the stream starts) is surfaced
    ///     as a single synthetic <c>error</c> event whose <c>ErrorCode</c> is the ProblemDetails <c>code</c> (or the HTTP status).
    ///     Transport failures and cancellation propagate as exceptions to the caller.
    /// </summary>
    public async IAsyncEnumerable<AgentEventDto> StreamTurnAsync(
        string sessionId,
        string text,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Relative($"/api/agents/sessions/{Uri.EscapeDataString(sessionId)}/turns/stream"))
        {
            Content = JsonContent.Create(new SendTurnRequestDto(text), options: Json),
        };
        request.SetBrowserResponseStreamingEnabled(true); // Blazor WASM: hand out the body as it arrives instead of buffering it

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var problem = await ReadProblemAsync(response, ct);
            yield return new AgentEventDto(
                ErrorEventKind,
                ErrorCode: problem.Code ?? ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture),
                ErrorMessage: problem.Message);
            yield break;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await foreach (var (_, data) in SseReader.ReadAsync(stream, ct))
        {
            var dto = JsonSerializer.Deserialize<AgentEventDto>(data, Json);
            if (dto is not null)
            {
                yield return dto;
            }
        }
    }

    private async Task<Result<T>> GetAsync<T>(string url, CancellationToken ct) where T : class
    {
        try
        {
            using var response = await http.GetAsync(Relative(url), ct);
            if (!response.IsSuccessStatusCode)
            {
                return Result.Failure<T>(await ReadProblemMessageAsync(response, ct));
            }

            var result = await response.Content.ReadFromJsonAsync<T>(Json, ct);
            return result is not null
                ? Result.Success(result)
                : Result.Failure<T>("No data returned from server");
        }
        catch (AccessTokenNotAvailableException)
        {
            return Result.Failure<T>(LoginRequired);
        }
        catch (HttpRequestException ex)
        {
            return Result.Failure<T>($"API error: {ex.Message}");
        }
    }

    private static Uri Relative(string url) => new(url, UriKind.Relative);

    private static async Task<string> ReadProblemMessageAsync(HttpResponseMessage response, CancellationToken ct) =>
        (await ReadProblemAsync(response, ct)).Message;

    /// <summary>
    ///     Extracts <c>code</c> and a human-readable message from a ProblemDetails body; falls back to the HTTP status when the
    ///     body is not JSON (proxies, auth middleware).
    /// </summary>
    private static async Task<(string? Code, string Message)> ReadProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var fallback = $"API error: {(int)response.StatusCode} {response.ReasonPhrase}";
        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch (HttpRequestException)
        {
            return (null, fallback);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return (null, fallback);
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, fallback);
            }

            var code = TryGetString(doc.RootElement, "code");
            var message = TryGetString(doc.RootElement, "detail") ?? TryGetString(doc.RootElement, "title") ?? fallback;
            return (code, message);
        }
        catch (JsonException)
        {
            return (null, fallback);
        }
    }

    private static string? TryGetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
