using System.Text.Json;
using Daedalus.Agents.Api;
using Daedalus.Api.Agents;
using Daedalus.Application.DTOs.Agents;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Formatters;
using Thalos;
using ZeroAlloc.Authorization;

namespace Daedalus.Api.Endpoints;

/// <summary>
///     Sessions and turns for Thalos agents. Streaming turns use Server-Sent Events. Ownership is enforced by the runtime
///     (a foreign session answers 404, never 403, so ids cannot be probed); these endpoints mirror that for their own reads.
/// </summary>
/// <remarks>
///     <para>
///         Ported from <c>AgentSessionsController</c> on the pattern of <see cref="AgentMemoriesEndpoints"/> (R38, R42, R50).
///         The group shares <c>api/agents</c> with <see cref="AgentEndpoints"/>, as the two controllers shared the route,
///         and carries <see cref="ProblemContentType.Json"/>, so every problem is <c>application/json</c>, built by
///         <see cref="ApiProblem.Of"/> or <see cref="AgentErrorResults.ToProblem"/>.
///     </para>
///     <para>
///         The stream endpoint carries <see cref="ProblemContentType.ProblemJson"/> of its own: the controller action's
///         <c>[Produces("text/event-stream")]</c> replaced the controller's, so its binding problem was
///         <c>application/problem+json</c> (R18). It declares <c>text/event-stream</c> through
///         <see cref="EventStreamProducesAttribute"/>, after that record, so it is the endpoint's last content type (R12).
///     </para>
/// </remarks>
public static partial class AgentSessionsEndpoints
{
    private const string AdminRole = "admin";
    private const string EventStreamContentType = "text/event-stream";
    private const int DefaultSkip = 0;
    private const int DefaultTake = 20;
    private const int MaxTake = 100;

    /// <summary>Maps the session routes under <c>api/agents</c>, under the <c>AgentUse</c> policy.</summary>
    public static RouteGroupBuilder MapAgentSessionsEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapApiGroup("api/agents")
            .RequireAuthorization("AgentUse")
            .WithTags("AgentSessions")
            .WithMetadata(ProblemContentType.Json);

        group.MapPost("{agentId}/sessions", CreateSession)
            .ProducesProblem(StatusCodes.Status400BadRequest, "application/json")
            .ProducesProblem(StatusCodes.Status404NotFound, "application/json");

        group.MapGet("sessions", ListSessions)
            .WithQueryParameter("skip", DefaultSkip)
            .WithQueryParameter("take", DefaultTake);

        group.MapGet("sessions/{sessionId}", GetSession)
            .ProducesProblem(StatusCodes.Status404NotFound, "application/json");

        group.MapPost("sessions/{sessionId}/turns", RunTurn)
            .RequireRateLimiting("llm-operations")
            .ProducesProblem(StatusCodes.Status400BadRequest, "application/json")
            .ProducesProblem(StatusCodes.Status404NotFound, "application/json")
            .ProducesProblem(StatusCodes.Status409Conflict, "application/json")
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity, "application/json");

        group.MapPost("sessions/{sessionId}/turns/stream", RunTurnStream)
            .RequireRateLimiting("llm-operations")
            .Produces(StatusCodes.Status200OK)
            .WithMetadata(ProblemContentType.ProblemJson, new EventStreamProducesAttribute());

        group.MapDelete("sessions/{sessionId}", CloseSession)
            .ProducesProblem(StatusCodes.Status404NotFound, "application/json")
            .ProducesProblem(StatusCodes.Status409Conflict, "application/json");

        return group;
    }

    /// <summary>Creates a session for <paramref name="agentId"/> owned by the caller.</summary>
    public static async Task<Results<Created<CreateAgentSessionResponseDto>, ApiProblemResult>> CreateSession(
        string agentId, HttpContext context, [FromServices] IAgentRuntime runtime, [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (!HttpSecurityContextFactory.TryCreate(context.User, out var caller))
        {
            return ApiProblem.Of(context, StatusCodes.Status401Unauthorized);
        }

        if (!AgentId.TryParse(agentId, null, out var id))
        {
            return AgentError.Validation("agentId is not a valid id.").ToProblem(context);
        }

        var result = await runtime.CreateSessionAsync(id, caller, ct);
        if (result.IsFailure)
        {
            return result.Error.ToProblem(context);
        }

        var created = result.Value;
        var logger = loggerFactory.CreateLogger(typeof(AgentSessionsEndpoints));
        Log.SessionCreated(logger, created, id, caller.Id);
        var sessionId = created.ToString();
        return TypedResults.Created(
            ApiLocation.For(context.Request, $"/api/agents/sessions/{sessionId}"),
            new CreateAgentSessionResponseDto(sessionId));
    }

    /// <summary>The caller's sessions, newest first.</summary>
    public static async Task<Results<Ok<IReadOnlyList<AgentSessionDto>>, ApiProblemResult>> ListSessions(
        ApiQuery<int> skip, ApiQuery<int> take, HttpContext context, [FromServices] IAgentSessionStore store, CancellationToken ct)
    {
        if (!HttpSecurityContextFactory.TryCreate(context.User, out var caller))
        {
            return ApiProblem.Of(context, StatusCodes.Status401Unauthorized);
        }

        var result = await store.ListAsync(
            caller.Id, Math.Max(0, skip.ValueOr(DefaultSkip)), Math.Clamp(take.ValueOr(DefaultTake), 1, MaxTake), ct);
        return result.IsFailure
            ? result.Error.ToProblem(context)
            : TypedResults.Ok<IReadOnlyList<AgentSessionDto>>(result.Value.Select(AgentDtoMapper.ToDto).ToList());
    }

    /// <summary>Session header plus transcript. Owner or admin only; anyone else gets 404.</summary>
    public static async Task<Results<Ok<AgentSessionDetailDto>, ApiProblemResult>> GetSession(
        string sessionId, HttpContext context, [FromServices] IAgentSessionStore store, [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (!HttpSecurityContextFactory.TryCreate(context.User, out var caller))
        {
            return ApiProblem.Of(context, StatusCodes.Status401Unauthorized);
        }

        if (!SessionId.TryParse(sessionId, null, out var id))
        {
            return AgentError.Validation("sessionId is not a valid id.").ToProblem(context);
        }

        var session = await store.GetAsync(id, ct);
        if (session.IsFailure)
        {
            return session.Error.ToProblem(context);
        }

        if (!IsOwnerOrAdmin(session.Value, caller))
        {
            Log.ForeignSessionAccess(loggerFactory.CreateLogger(typeof(AgentSessionsEndpoints)), caller.Id, id);
            return AgentError.SessionNotFound(id).ToProblem(context);
        }

        var messages = await store.LoadMessagesAsync(id, ct);
        if (messages.IsFailure)
        {
            return messages.Error.ToProblem(context);
        }

        return TypedResults.Ok(new AgentSessionDetailDto(AgentDtoMapper.ToDto(session.Value), AgentDtoMapper.ToDtos(messages.Value)));
    }

    /// <summary>Runs one turn and returns the buffered result.</summary>
    public static async Task<Results<Ok<AgentTurnResultDto>, ApiProblemResult>> RunTurn(
        string sessionId, ApiBody<SendTurnRequestDto> request, HttpContext context, [FromServices] IAgentRuntime runtime,
        CancellationToken ct)
    {
        if (!HttpSecurityContextFactory.TryCreate(context.User, out var caller))
        {
            return ApiProblem.Of(context, StatusCodes.Status401Unauthorized);
        }

        if (!SessionId.TryParse(sessionId, null, out var id))
        {
            return AgentError.Validation("sessionId is not a valid id.").ToProblem(context);
        }

        var result = await runtime.RunTurnAsync(new AgentTurnRequest(id, request.Value!.Text, caller), ct);
        return result.IsFailure ? result.Error.ToProblem(context) : TypedResults.Ok(AgentDtoMapper.ToDto(result.Value));
    }

    /// <summary>
    ///     Runs one turn as Server-Sent Events: <c>event: &lt;kind&gt;</c> + <c>data: &lt;AgentEventDto JSON&gt;</c> per event
    ///     (kinds: <c>text-delta</c>, <c>tool-call</c>, <c>tool-result</c>, <c>usage</c>, <c>done</c>, <c>error</c>). The stream
    ///     ends after <c>done</c> or <c>error</c>; every event is flushed as soon as it is produced.
    /// </summary>
    /// <remarks>
    ///     Writes the response itself, so it returns a bare <see cref="Task"/>: an unauthenticated caller gets a bodiless
    ///     401, as from the controller. A client that goes away cancels <paramref name="ct"/>, the request's abort token,
    ///     which ends the runtime's turn; the host's exception middleware treats the cancellation as an aborted request.
    /// </remarks>
    public static async Task RunTurnStream(
        string sessionId, ApiBody<SendTurnRequestDto> request, HttpContext context, [FromServices] IAgentRuntime runtime,
        CancellationToken ct)
    {
        var response = context.Response;
        if (!HttpSecurityContextFactory.TryCreate(context.User, out var caller))
        {
            response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = EventStreamContentType;
        response.Headers.CacheControl = "no-cache";
        response.Headers.Append("X-Accel-Buffering", "no");
        // Compression and other buffering middleware would hold events back until the turn ends.
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        if (!SessionId.TryParse(sessionId, null, out var id))
        {
            await WriteEventAsync(response, new AgentEventDto(AgentEventKinds.Error, ErrorCode: nameof(AgentErrorCode.Validation), ErrorMessage: "sessionId is not a valid id."), ct);
            return;
        }

        // Headers must reach the client before the first model token: the Web client's resilience handler times out an
        // attempt after 10 s and would re-POST the turn otherwise. A comment frame is valid SSE and ignored by readers.
        await response.StartAsync(ct);
        await response.WriteAsync(": connected\n\n", ct);
        await response.Body.FlushAsync(ct);

        await foreach (var agentEvent in runtime.RunTurnStreamingAsync(new AgentTurnRequest(id, request.Value!.Text, caller), ct))
        {
            await WriteEventAsync(response, AgentDtoMapper.ToDto(agentEvent), ct);
        }
    }

    /// <summary>Closes the session (terminal). Owner or admin only.</summary>
    public static async Task<Results<NoContent, ApiProblemResult>> CloseSession(
        string sessionId, HttpContext context, [FromServices] IAgentRuntime runtime, CancellationToken ct)
    {
        if (!HttpSecurityContextFactory.TryCreate(context.User, out var caller))
        {
            return ApiProblem.Of(context, StatusCodes.Status401Unauthorized);
        }

        if (!SessionId.TryParse(sessionId, null, out var id))
        {
            return AgentError.Validation("sessionId is not a valid id.").ToProblem(context);
        }

        var result = await runtime.CloseSessionAsync(id, caller, ct);
        return result.IsFailure ? result.Error.ToProblem(context) : TypedResults.NoContent();
    }

    private static bool IsOwnerOrAdmin(AgentSessionRecord session, ISecurityContext caller) =>
        string.Equals(session.OwnerId, caller.Id, StringComparison.Ordinal) || caller.Roles.Contains(AdminRole);

    private static async Task WriteEventAsync(HttpResponse response, AgentEventDto dto, CancellationToken ct)
    {
        // JSON never contains a raw newline (they are escaped), so one data line per event is enough. The events keep
        // their own context, which omits unset members (R25).
        var payload = JsonSerializer.Serialize(dto, AgentEventStreamJsonContext.Default.AgentEventDto);
        await response.WriteAsync($"event: {dto.Kind}\ndata: {payload}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }

    /// <summary>
    ///     <c>[Produces("text/event-stream")]</c> as the controller action declared it: the endpoint's content type, with
    ///     no media type stamped on its response types (R50).
    /// </summary>
    /// <remarks>
    ///     MVC's API explorer adds a <c>[Produces]</c> content type only to responses that have a body type, so the
    ///     stream's bodiless 200 had none. The minimal-API explorer adds it to every response of the endpoint, so a plain
    ///     <see cref="ProducesAttribute"/> would describe the 200 as <c>text/event-stream</c>, a change to the endpoint
    ///     table and the OpenAPI document. This attribute keeps the content type and leaves the response formats alone,
    ///     as MVC described the action.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
    private sealed class EventStreamProducesAttribute() : ProducesAttribute(EventStreamContentType), IApiResponseMetadataProvider
    {
        void IApiResponseMetadataProvider.SetContentTypes(MediaTypeCollection contentTypes)
        {
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 300, Level = LogLevel.Information, Message = "Agent session {SessionId} created for agent {AgentId} by {Caller}")]
        public static partial void SessionCreated(ILogger logger, SessionId sessionId, AgentId agentId, string caller);

        [LoggerMessage(EventId = 301, Level = LogLevel.Warning, Message = "Caller {Caller} attempted to read session {SessionId} it does not own; answered 404")]
        public static partial void ForeignSessionAccess(ILogger logger, string caller, SessionId sessionId);
    }
}
