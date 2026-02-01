using Daedalus.Application.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TaskDto = Daedalus.Application.DTOs.TaskDto;

namespace Daedalus.Api.Controllers;

/// <summary>
///     API endpoints for PRD generation and task conversion.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/[controller]")]
[Authorize]
[Produces("application/json")]
public sealed partial class PrdController(IPrdService prdService, ILogger<PrdController> logger) : ControllerBase
{
    [LoggerMessage(EventId = 100, Level = LogLevel.Information, Message = "Generating PRD for project {ProjectId}")]
    private static partial void LogGeneratingPrd(ILogger logger, Guid projectId);

    [LoggerMessage(EventId = 101, Level = LogLevel.Warning, Message = "PRD generation failed: {Error}")]
    private static partial void LogPrdGenerationFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 102, Level = LogLevel.Information,
        Message = "PRD generated successfully with {ItemCount} items")]
    private static partial void LogPrdGeneratedSuccessfully(ILogger logger, int itemCount);

    [LoggerMessage(EventId = 103, Level = LogLevel.Error, Message = "Error generating PRD for project {ProjectId}")]
    private static partial void LogErrorGeneratingPrd(ILogger logger, Guid projectId, Exception ex);

    [LoggerMessage(EventId = 104, Level = LogLevel.Information,
        Message = "Converting {ItemCount} PRD items to tasks for project {ProjectId}")]
    private static partial void LogConvertingPrdItems(ILogger logger, int itemCount, Guid projectId);

    [LoggerMessage(EventId = 105, Level = LogLevel.Warning, Message = "PRD to tasks conversion failed: {Error}")]
    private static partial void LogConversionFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 106, Level = LogLevel.Information,
        Message = "Successfully converted {ItemCount} PRD items to tasks")]
    private static partial void LogConversionSuccessful(ILogger logger, int itemCount);

    [LoggerMessage(EventId = 107, Level = LogLevel.Error,
        Message = "Error converting PRD items to tasks for project {ProjectId}")]
    private static partial void LogErrorConvertingPrdItems(ILogger logger, Guid projectId, Exception ex);

    /// <summary>
    ///     Generates a PRD from user requirements using LLM with PRD agent mode.
    /// </summary>
    /// <param name="request">The PRD request containing user requirements.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The generated PRD with structured features and tasks.</returns>
    [Authorize(Policy = "CodeAnalysis")]
    [EnableRateLimiting("llm-operations")]
    [HttpPost("generate")]
    [ProducesResponseType(typeof(PrdResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GeneratePrd(
        [FromBody] PrdRequestDto request,
        CancellationToken ct = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.UserRequirements))
        {
            return BadRequest(new { error = "User requirements cannot be empty" });
        }

        try
        {
            LogGeneratingPrd(logger, request.ProjectId);

            var result = await prdService.GeneratePrdAsync(
                request.ProjectId,
                request.UserRequirements,
                request.Context,
                ct);

            if (result.IsFailure)
            {
                LogPrdGenerationFailed(logger, result.Error);
                return BadRequest(new { error = result.Error });
            }

            LogPrdGeneratedSuccessfully(logger, result.Value.AllItems.Count);

            return Ok(result.Value);
        }
        catch (Exception ex)
        {
            LogErrorGeneratingPrd(logger, request.ProjectId, ex);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new { error = "Error generating PRD", details = ex.Message });
        }
    }

    /// <summary>
    ///     Converts selected PRD items to tasks and persists them to the database.
    /// </summary>
    /// <param name="request">The conversion request with selected PRD items.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created tasks.</returns>
    [Authorize(Policy = "TaskManagement")]
    [EnableRateLimiting("write-operations")]
    [HttpPost("convert-to-tasks")]
    [ProducesResponseType(typeof(List<TaskDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ConvertToTasks(
        [FromBody] PrdConversionRequestDto request,
        CancellationToken ct = default)
    {
        if (request == null || request.PrdItems.Count == 0)
        {
            return BadRequest(new { error = "No PRD items to convert" });
        }

        try
        {
            LogConvertingPrdItems(logger, request.PrdItems.Count, request.ProjectId);

            var result = await prdService.ConvertToTasksAsync(
                request.ProjectId,
                request.PrdItems,
                ct);

            if (result.IsFailure)
            {
                LogConversionFailed(logger, result.Error);
                return BadRequest(new { error = result.Error });
            }

            LogConversionSuccessful(logger, result.Value.Count);

            return Ok(result.Value);
        }
        catch (Exception ex)
        {
            LogErrorConvertingPrdItems(logger, request.ProjectId, ex);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new { error = "Error converting PRD items to tasks", details = ex.Message });
        }
    }
}
