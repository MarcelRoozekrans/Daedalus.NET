using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Daedalus.Api.Controllers;

/// <summary>API endpoints for accessing task execution data.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/[controller]")]
[Authorize]
[Produces("application/json")]
public sealed partial class TaskExecutionsController(
    ITaskExecutionQueryService executionService,
    ILogger<TaskExecutionsController> logger) : ControllerBase
{
    [LoggerMessage(EventId = 100, Level = LogLevel.Error, Message = "Error retrieving executions for task {TaskId}")]
    private static partial void LogErrorRetrievingExecutionsForTask(ILogger logger, Guid taskId, Exception ex);

    [LoggerMessage(EventId = 101, Level = LogLevel.Error,
        Message = "Error retrieving executions for session {SessionId}")]
    private static partial void LogErrorRetrievingExecutionsForSession(ILogger logger, Guid sessionId, Exception ex);

    /// <summary>Get all executions for a specific task.</summary>
    [Authorize(Policy = "TaskRead")]
    [HttpGet("task/{taskId:guid}")]
    [ProducesResponseType(typeof(PagedResultDto<TaskExecutionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetByTaskId(Guid taskId, [FromQuery] int page = 1, [FromQuery] int pageSize = 10,
        CancellationToken ct = default)
    {
        try
        {
            var result = await executionService.GetByTaskIdAsync(taskId, page, pageSize, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            LogErrorRetrievingExecutionsForTask(logger, taskId, ex);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Internal server error" });
        }
    }

    /// <summary>Get all executions for a specific session.</summary>
    [Authorize(Policy = "TaskRead")]
    [HttpGet("session/{sessionId:guid}")]
    [ProducesResponseType(typeof(PagedResultDto<TaskExecutionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetBySessionId(Guid sessionId, [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10, CancellationToken ct = default)
    {
        try
        {
            var result = await executionService.GetBySessionIdAsync(sessionId, page, pageSize, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            LogErrorRetrievingExecutionsForSession(logger, sessionId, ex);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Internal server error" });
        }
    }
}
