using System.Text.Json;
using Daedalus.Agents.Api;
using Daedalus.Api.Agents;
using Daedalus.Application.DTOs.Agents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Thalos;
using ZeroAlloc.Authorization;

namespace Daedalus.Api.Controllers;

/// <summary>
///     Sessions and turns for Thalos agents. Streaming turns use Server-Sent Events. Ownership is enforced by the runtime
///     (a foreign session answers 404, never 403, so ids cannot be probed); this controller mirrors that for its own reads.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/agents")]
[Authorize(Policy = "AgentUse")]
[Produces("application/json")]
public sealed partial class AgentSessionsController(
    IAgentRuntime runtime,
    IAgentSessionStore store,
    ILogger<AgentSessionsController> logger) : ControllerBase
{
    private const string AdminRole = "admin";
    private const string EventStreamContentType = "text/event-stream";

    /// <summary>Creates a session for <paramref name="agentId"/> owned by the caller.</summary>
    [HttpPost("{agentId}/sessions")]
    [ProducesResponseType(typeof(CreateAgentSessionResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreateSession(string agentId, CancellationToken ct)
    {
        if (!HttpSecurityContextFactory.TryCreate(User, out var caller))
        {
            return Unauthorized();
        }

        if (!AgentId.TryParse(agentId, null, out var id))
        {
            return AgentError.Validation("agentId is not a valid id.").ToActionResult(this);
        }

        var result = await runtime.CreateSessionAsync(id, caller, ct);
        if (result.IsFailure)
        {
            return result.Error.ToActionResult(this);
        }

        LogSessionCreated(logger, result.Value, id, caller.Id);
        return CreatedAtAction(
            nameof(GetSession),
            new { sessionId = result.Value.ToString() },
            new CreateAgentSessionResponseDto(result.Value.ToString()));
    }

    /// <summary>The caller's sessions, newest first.</summary>
    [HttpGet("sessions")]
    [ProducesResponseType(typeof(IReadOnlyList<AgentSessionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListSessions([FromQuery] int skip = 0, [FromQuery] int take = 20, CancellationToken ct = default)
    {
        if (!HttpSecurityContextFactory.TryCreate(User, out var caller))
        {
            return Unauthorized();
        }

        var result = await store.ListAsync(caller.Id, Math.Max(0, skip), Math.Clamp(take, 1, 100), ct);
        return result.IsFailure
            ? result.Error.ToActionResult(this)
            : Ok(result.Value.Select(AgentDtoMapper.ToDto).ToList());
    }

    /// <summary>Session header plus transcript. Owner or admin only; anyone else gets 404.</summary>
    [HttpGet("sessions/{sessionId}")]
    [ProducesResponseType(typeof(AgentSessionDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSession(string sessionId, CancellationToken ct)
    {
        if (!HttpSecurityContextFactory.TryCreate(User, out var caller))
        {
            return Unauthorized();
        }

        if (!SessionId.TryParse(sessionId, null, out var id))
        {
            return AgentError.Validation("sessionId is not a valid id.").ToActionResult(this);
        }

        var session = await store.GetAsync(id, ct);
        if (session.IsFailure)
        {
            return session.Error.ToActionResult(this);
        }

        if (!IsOwnerOrAdmin(session.Value, caller))
        {
            LogForeignSessionAccess(logger, caller.Id, id);
            return AgentError.SessionNotFound(id).ToActionResult(this);
        }

        var messages = await store.LoadMessagesAsync(id, ct);
        if (messages.IsFailure)
        {
            return messages.Error.ToActionResult(this);
        }

        return Ok(new AgentSessionDetailDto(AgentDtoMapper.ToDto(session.Value), AgentDtoMapper.ToDtos(messages.Value)));
    }

    /// <summary>Runs one turn and returns the buffered result.</summary>
    [HttpPost("sessions/{sessionId}/turns")]
    [EnableRateLimiting("llm-operations")]
    [ProducesResponseType(typeof(AgentTurnResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> RunTurn(string sessionId, [FromBody] SendTurnRequestDto request, CancellationToken ct)
    {
        if (!HttpSecurityContextFactory.TryCreate(User, out var caller))
        {
            return Unauthorized();
        }

        if (!SessionId.TryParse(sessionId, null, out var id))
        {
            return AgentError.Validation("sessionId is not a valid id.").ToActionResult(this);
        }

        var result = await runtime.RunTurnAsync(new AgentTurnRequest(id, request.Text, caller), ct);
        return result.IsFailure ? result.Error.ToActionResult(this) : Ok(AgentDtoMapper.ToDto(result.Value));
    }

    /// <summary>
    ///     Runs one turn as Server-Sent Events: <c>event: &lt;kind&gt;</c> + <c>data: &lt;AgentEventDto JSON&gt;</c> per event
    ///     (kinds: <c>text-delta</c>, <c>tool-call</c>, <c>tool-result</c>, <c>usage</c>, <c>done</c>, <c>error</c>). The stream
    ///     ends after <c>done</c> or <c>error</c>; every event is flushed as soon as it is produced.
    /// </summary>
    [HttpPost("sessions/{sessionId}/turns/stream")]
    [EnableRateLimiting("llm-operations")]
    [Produces(EventStreamContentType)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task RunTurnStream(string sessionId, [FromBody] SendTurnRequestDto request, CancellationToken ct)
    {
        if (!HttpSecurityContextFactory.TryCreate(User, out var caller))
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = EventStreamContentType;
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Append("X-Accel-Buffering", "no");
        // Compression and other buffering middleware would hold events back until the turn ends.
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        if (!SessionId.TryParse(sessionId, null, out var id))
        {
            await WriteEventAsync(new AgentEventDto(AgentEventKinds.Error, ErrorCode: nameof(AgentErrorCode.Validation), ErrorMessage: "sessionId is not a valid id."), ct);
            return;
        }

        // Headers must reach the client before the first model token: the Web client's resilience handler times out an
        // attempt after 10 s and would re-POST the turn otherwise. A comment frame is valid SSE and ignored by readers.
        await Response.StartAsync(ct);
        await Response.WriteAsync(": connected\n\n", ct);
        await Response.Body.FlushAsync(ct);

        await foreach (var agentEvent in runtime.RunTurnStreamingAsync(new AgentTurnRequest(id, request.Text, caller), ct))
        {
            await WriteEventAsync(AgentDtoMapper.ToDto(agentEvent), ct);
        }
    }

    /// <summary>Closes the session (terminal). Owner or admin only.</summary>
    [HttpDelete("sessions/{sessionId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CloseSession(string sessionId, CancellationToken ct)
    {
        if (!HttpSecurityContextFactory.TryCreate(User, out var caller))
        {
            return Unauthorized();
        }

        if (!SessionId.TryParse(sessionId, null, out var id))
        {
            return AgentError.Validation("sessionId is not a valid id.").ToActionResult(this);
        }

        var result = await runtime.CloseSessionAsync(id, caller, ct);
        return result.IsFailure ? result.Error.ToActionResult(this) : NoContent();
    }

    private static bool IsOwnerOrAdmin(AgentSessionRecord session, ISecurityContext caller) =>
        string.Equals(session.OwnerId, caller.Id, StringComparison.Ordinal) || caller.Roles.Contains(AdminRole);

    private async Task WriteEventAsync(AgentEventDto dto, CancellationToken ct)
    {
        // JSON never contains a raw newline (they are escaped), so one data line per event is enough.
        var payload = JsonSerializer.Serialize(dto, ApiJsonSerializerContext.Default.AgentEventDto);
        await Response.WriteAsync($"event: {dto.Kind}\ndata: {payload}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }

    [LoggerMessage(EventId = 300, Level = LogLevel.Information, Message = "Agent session {SessionId} created for agent {AgentId} by {Caller}")]
    private static partial void LogSessionCreated(ILogger logger, SessionId sessionId, AgentId agentId, string caller);

    [LoggerMessage(EventId = 301, Level = LogLevel.Warning, Message = "Caller {Caller} attempted to read session {SessionId} it does not own; answered 404")]
    private static partial void LogForeignSessionAccess(ILogger logger, string caller, SessionId sessionId);
}
