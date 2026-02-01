using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Daedalus.Api.Controllers;

/// <summary>API endpoints for accessing execution session data.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/[controller]")]
[Authorize]
[Produces("application/json")]
public sealed partial class ExecutionSessionsController(
    IExecutionSessionQueryService sessionService,
    ILogger<ExecutionSessionsController> logger) : ControllerBase
{
    [LoggerMessage(EventId = 100, Level = LogLevel.Error, Message = "Error retrieving execution sessions")]
    private static partial void LogErrorRetrievingSessions(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 101, Level = LogLevel.Error, Message = "Error retrieving session {SessionId}")]
    private static partial void LogErrorRetrievingSession(ILogger logger, Guid sessionId, Exception ex);

    [LoggerMessage(EventId = 102, Level = LogLevel.Error, Message = "Error retrieving active sessions")]
    private static partial void LogErrorRetrievingActiveSessions(ILogger logger, Exception ex);

    /// <summary>Get all execution sessions with pagination.</summary>
    [Authorize(Policy = "TaskRead")]
    [HttpGet]
    [ProducesResponseType(typeof(PagedResultDto<ExecutionSessionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllSessions([FromQuery] int page = 1, [FromQuery] int pageSize = 10,
        CancellationToken ct = default)
    {
        try
        {
            var result = await sessionService.GetAllAsync(page, pageSize, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            LogErrorRetrievingSessions(logger, ex);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Internal server error" });
        }
    }

    /// <summary>Get a specific execution session by ID.</summary>
    [Authorize(Policy = "TaskRead")]
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ExecutionSessionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSessionById(Guid id, CancellationToken ct = default)
    {
        try
        {
            var session = await sessionService.GetByIdAsync(id, ct);
            if (session is null)
            {
                return NotFound(new { error = $"Session with ID {id} not found" });
            }

            return Ok(session);
        }
        catch (Exception ex)
        {
            LogErrorRetrievingSession(logger, id, ex);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Internal server error" });
        }
    }

    /// <summary>Get active execution sessions.</summary>
    [Authorize(Policy = "TaskRead")]
    [HttpGet("active")]
    [ProducesResponseType(typeof(PagedResultDto<ExecutionSessionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetActiveSessions([FromQuery] int page = 1, [FromQuery] int pageSize = 10,
        CancellationToken ct = default)
    {
        try
        {
            var result = await sessionService.GetActiveAsync(page, pageSize, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            LogErrorRetrievingActiveSessions(logger, ex);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Internal server error" });
        }
    }
}
