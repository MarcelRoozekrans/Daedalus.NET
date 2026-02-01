using Daedalus.Application.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Daedalus.Api.Controllers;

/// <summary>API endpoints for Ralph Loop configuration management.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/ralph-config")]
[Authorize]
[Produces("application/json")]
public sealed partial class RalphConfigController(
    IOptionsMonitor<RalphLoopConfiguration> loopOptions,
    ILogger<RalphConfigController> logger) : ControllerBase
{
    [LoggerMessage(EventId = 100, Level = LogLevel.Information,
        Message = "Ralph config updated: delay={DelayMs}ms, maxFailures={MaxFailures}, timeout={Timeout}s")]
    private static partial void LogRalphConfigUpdated(ILogger logger, int delayMs, int maxFailures, int timeout);

    /// <summary>Get current Ralph Loop configuration.</summary>
    [Authorize(Policy = "TaskRead")]
    [HttpGet]
    [ProducesResponseType(typeof(RalphConfigDto), StatusCodes.Status200OK)]
    public IActionResult GetConfig()
    {
        var config = loopOptions.CurrentValue;

        var dto = new RalphConfigDto(
            config.IterationDelayMs,
            config.MaxConsecutiveFailures,
            config.MaxIterations,
            config.RequestTimeoutSeconds,
            config.EnableDetailedLogging,
            new RalphPromptConfigDto(
                0,
                true,
                true,
                true,
                true,
                true,
                true));

        return Ok(dto);
    }

    /// <summary>Update Ralph Loop configuration at runtime.</summary>
    [Authorize(Policy = "Admin")]
    [EnableRateLimiting("write-operations")]
    [HttpPut]
    [ProducesResponseType(typeof(RalphConfigDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult UpdateConfig([FromBody] RalphConfigDto dto)
    {
        if (dto.IterationDelayMs < 0)
        {
            return BadRequest(new { error = "IterationDelayMs must be non-negative" });
        }

        if (dto.MaxConsecutiveFailures <= 0)
        {
            return BadRequest(new { error = "MaxConsecutiveFailures must be positive" });
        }

        // Update the in-memory options. Changes persist until restart.
        var config = loopOptions.CurrentValue;
        config.IterationDelayMs = dto.IterationDelayMs;
        config.MaxConsecutiveFailures = dto.MaxConsecutiveFailures;
        config.MaxIterations = dto.MaxIterations;
        config.RequestTimeoutSeconds = dto.RequestTimeoutSeconds;
        config.EnableDetailedLogging = dto.EnableDetailedLogging;

        LogRalphConfigUpdated(logger, dto.IterationDelayMs, dto.MaxConsecutiveFailures, dto.RequestTimeoutSeconds);

        return Ok(dto);
    }
}
