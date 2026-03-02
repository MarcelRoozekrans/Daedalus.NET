using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Daedalus.Application.Mappers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Daedalus.Api.Controllers;

/// <summary>
///     API endpoints for interactive brainstorming sessions.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/[controller]")]
[Authorize]
[Produces("application/json")]
public sealed partial class BrainstormController(
    IBrainstormService brainstormService,
    ILogger<BrainstormController> logger) : ControllerBase
{
    [LoggerMessage(EventId = 200, Level = LogLevel.Information,
        Message = "Creating brainstorm session for project {ProjectId}")]
    private static partial void LogCreatingSession(ILogger logger, Guid projectId);

    [LoggerMessage(EventId = 201, Level = LogLevel.Information,
        Message = "Retrieving brainstorm session {SessionId}")]
    private static partial void LogRetrievingSession(ILogger logger, Guid sessionId);

    [LoggerMessage(EventId = 202, Level = LogLevel.Information,
        Message = "Retrieving brainstorm sessions for project {ProjectId}")]
    private static partial void LogRetrievingSessions(ILogger logger, Guid projectId);

    [LoggerMessage(EventId = 203, Level = LogLevel.Information,
        Message = "Sending message to brainstorm session {SessionId}")]
    private static partial void LogSendingMessage(ILogger logger, Guid sessionId);

    [LoggerMessage(EventId = 204, Level = LogLevel.Information,
        Message = "Advancing phase for brainstorm session {SessionId}")]
    private static partial void LogAdvancingPhase(ILogger logger, Guid sessionId);

    [LoggerMessage(EventId = 205, Level = LogLevel.Information,
        Message = "Abandoning brainstorm session {SessionId}")]
    private static partial void LogAbandoningSession(ILogger logger, Guid sessionId);

    [LoggerMessage(EventId = 206, Level = LogLevel.Information,
        Message = "Generating tasks for brainstorm session {SessionId}")]
    private static partial void LogGeneratingTasks(ILogger logger, Guid sessionId);

    /// <summary>
    ///     Create a new brainstorm session for a project.
    /// </summary>
    [Authorize(Policy = "CodeAnalysis")]
    [EnableRateLimiting("llm-operations")]
    [HttpPost("sessions")]
    [ProducesResponseType(typeof(BrainstormSessionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateSession(
        [FromBody] CreateBrainstormSessionDto request,
        CancellationToken ct = default)
    {
        LogCreatingSession(logger, request.ProjectId);

        var result = await brainstormService.CreateSessionAsync(request.ProjectId, ct);

        return result.IsSuccess
            ? Ok(BrainstormDtoMapper.ToDto(result.Value))
            : BadRequest(new { error = result.Error });
    }

    /// <summary>
    ///     Get a brainstorm session by ID including all messages.
    /// </summary>
    [Authorize(Policy = "CodeAnalysisRead")]
    [HttpGet("sessions/{sessionId:guid}")]
    [ProducesResponseType(typeof(BrainstormSessionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSession(Guid sessionId, CancellationToken ct = default)
    {
        LogRetrievingSession(logger, sessionId);

        var result = await brainstormService.GetSessionAsync(sessionId, ct);

        return result.IsSuccess
            ? Ok(BrainstormDtoMapper.ToDto(result.Value))
            : NotFound(new { error = result.Error });
    }

    /// <summary>
    ///     Get all brainstorm sessions for a project.
    /// </summary>
    [Authorize(Policy = "CodeAnalysisRead")]
    [HttpGet("sessions")]
    [ProducesResponseType(typeof(IReadOnlyList<BrainstormSessionSummaryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSessions(
        [FromQuery] Guid projectId,
        CancellationToken ct = default)
    {
        LogRetrievingSessions(logger, projectId);

        var result = await brainstormService.GetSessionsByProjectAsync(projectId, ct);

        if (result.IsFailure)
            return BadRequest(new { error = result.Error });

        var summaries = result.Value
            .Select(BrainstormDtoMapper.ToSummaryDto)
            .ToList();

        return Ok(summaries);
    }

    /// <summary>
    ///     Send a user message to a brainstorm session.
    /// </summary>
    [Authorize(Policy = "CodeAnalysis")]
    [EnableRateLimiting("llm-operations")]
    [HttpPost("sessions/{sessionId:guid}/messages")]
    [ProducesResponseType(typeof(BrainstormMessageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SendMessage(
        Guid sessionId,
        [FromBody] SendBrainstormMessageDto request,
        CancellationToken ct = default)
    {
        LogSendingMessage(logger, sessionId);

        var result = await brainstormService.SendMessageAsync(sessionId, request.Content, ct);

        return result.IsSuccess
            ? Ok(BrainstormDtoMapper.ToDto(result.Value))
            : BadRequest(new { error = result.Error });
    }

    /// <summary>
    ///     Advance the brainstorm session to the next phase.
    /// </summary>
    [Authorize(Policy = "CodeAnalysis")]
    [EnableRateLimiting("llm-operations")]
    [HttpPost("sessions/{sessionId:guid}/advance")]
    [ProducesResponseType(typeof(BrainstormSessionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> AdvancePhase(Guid sessionId, CancellationToken ct = default)
    {
        LogAdvancingPhase(logger, sessionId);

        var result = await brainstormService.AdvancePhaseAsync(sessionId, ct);

        return result.IsSuccess
            ? Ok(BrainstormDtoMapper.ToDto(result.Value))
            : BadRequest(new { error = result.Error });
    }

    /// <summary>
    ///     Abandon a brainstorm session.
    /// </summary>
    [Authorize(Policy = "CodeAnalysis")]
    [HttpPost("sessions/{sessionId:guid}/abandon")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> AbandonSession(Guid sessionId, CancellationToken ct = default)
    {
        LogAbandoningSession(logger, sessionId);

        var result = await brainstormService.AbandonSessionAsync(sessionId, ct);

        return result.IsSuccess
            ? NoContent()
            : BadRequest(new { error = result.Error });
    }

    /// <summary>
    ///     Generate tasks from a completed brainstorm session (stub).
    /// </summary>
    [Authorize(Policy = "TaskManagement")]
    [EnableRateLimiting("write-operations")]
    [HttpPost("sessions/{sessionId:guid}/generate-tasks")]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult GenerateTasks(Guid sessionId)
    {
        LogGeneratingTasks(logger, sessionId);

        return BadRequest(new { error = "Not yet implemented" });
    }
}
