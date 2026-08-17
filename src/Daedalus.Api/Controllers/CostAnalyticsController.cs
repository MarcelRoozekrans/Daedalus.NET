using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Daedalus.Api.Controllers;

/// <summary>API endpoints for cost analytics and estimation.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/cost-analytics")]
[Authorize]
[Produces("application/json")]
public sealed partial class CostAnalyticsController(
    ICostAnalyticsService costService,
    ILogger<CostAnalyticsController> logger) : ControllerBase
{
    [LoggerMessage(EventId = 200, Level = LogLevel.Error, Message = "Error retrieving cost analytics")]
    private static partial void LogErrorRetrievingCosts(ILogger logger, Exception ex);

    /// <summary>Get overall cost summary.</summary>
    [Authorize(Policy = "TaskRead")]
    [HttpGet("summary")]
    [ProducesResponseType(typeof(CostSummaryDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSummary(CancellationToken ct = default)
    {
        try
        {
            var result = await costService.GetSummaryAsync(ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            LogErrorRetrievingCosts(logger, ex);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Internal server error" });
        }
    }

    /// <summary>Get cost breakdown by project.</summary>
    [Authorize(Policy = "TaskRead")]
    [HttpGet("by-project")]
    [ProducesResponseType(typeof(IReadOnlyList<ProjectCostDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetByProject(CancellationToken ct = default)
    {
        try
        {
            var result = await costService.GetCostsByProjectAsync(ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            LogErrorRetrievingCosts(logger, ex);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Internal server error" });
        }
    }

    /// <summary>Get per-task cost breakdown for a project.</summary>
    [Authorize(Policy = "TaskRead")]
    [HttpGet("by-project/{id:guid}")]
    [ProducesResponseType(typeof(IReadOnlyList<TaskCostDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetByProjectId(Guid id, CancellationToken ct = default)
    {
        try
        {
            var result = await costService.GetCostsByProjectIdAsync(id, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            LogErrorRetrievingCosts(logger, ex);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Internal server error" });
        }
    }

    /// <summary>Get per-task cost breakdown for a session.</summary>
    [Authorize(Policy = "TaskRead")]
    [HttpGet("by-session/{id:guid}")]
    [ProducesResponseType(typeof(IReadOnlyList<TaskCostDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetBySessionId(Guid id, CancellationToken ct = default)
    {
        try
        {
            var result = await costService.GetCostsBySessionIdAsync(id, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            LogErrorRetrievingCosts(logger, ex);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Internal server error" });
        }
    }

    /// <summary>Estimate cost for a planned Ralph run.</summary>
    [Authorize(Policy = "TaskRead")]
    [HttpGet("estimate")]
    [ProducesResponseType(typeof(CostEstimateDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> EstimateCost(
        [FromQuery] string modelId,
        [FromQuery] int maxIterations = 10,
        [FromQuery] int estimatedPromptTokens = 4000,
        CancellationToken ct = default)
    {
        try
        {
            var result = await costService.EstimateCostAsync(modelId, maxIterations, estimatedPromptTokens, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            LogErrorRetrievingCosts(logger, ex);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Internal server error" });
        }
    }

    /// <summary>Get configured model pricing.</summary>
    [Authorize(Policy = "TaskRead")]
    [HttpGet("pricing")]
    [ProducesResponseType(typeof(IReadOnlyList<ModelPricingDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPricing(CancellationToken ct = default)
    {
        try
        {
            var result = await costService.GetPricingAsync(ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            LogErrorRetrievingCosts(logger, ex);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Internal server error" });
        }
    }
}
