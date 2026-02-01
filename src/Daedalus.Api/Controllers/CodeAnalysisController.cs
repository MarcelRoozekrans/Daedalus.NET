using Daedalus.Application.Services.CodeAnalysis;
using Daedalus.Domain.CodeAnalysis;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Daedalus.Api.Controllers;

#pragma warning disable CA1054 // Uri parameters should not be strings
#pragma warning disable CA1056 // Uri properties should not be strings
#pragma warning disable S6960 // SonarQube complexity

/// <summary>
///     API endpoints for code analysis and Ralph Loop orchestration
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/[controller]")]
[Authorize]
[Produces("application/json")]
public sealed partial class CodeAnalysisController(
    ICodeAnalysisRepository codeAnalysisRepository,
    ILogger<CodeAnalysisController> logger) : ControllerBase
{
    [LoggerMessage(EventId = 100, Level = LogLevel.Information,
        Message = "Submitting analysis request for repository {RepositoryUrl}")]
    private static partial void LogSubmittingAnalysisRequest(ILogger logger, string repositoryUrl);

    [LoggerMessage(EventId = 101, Level = LogLevel.Information,
        Message = "Retrieving analysis status for request {RequestId}")]
    private static partial void LogRetrievingAnalysisStatus(ILogger logger, Guid requestId);

    [LoggerMessage(EventId = 102, Level = LogLevel.Information,
        Message = "Processing iteration for analysis request {RequestId}")]
    private static partial void LogProcessingIteration(ILogger logger, Guid requestId);

    [LoggerMessage(EventId = 103, Level = LogLevel.Information, Message = "Finalizing analysis request {RequestId}")]
    private static partial void LogFinalizingAnalysisRequest(ILogger logger, Guid requestId);

    [LoggerMessage(EventId = 104, Level = LogLevel.Information, Message = "Retrieving next pending analysis request")]
    private static partial void LogRetrievingNextPending(ILogger logger);

    [LoggerMessage(EventId = 105, Level = LogLevel.Information,
        Message = "Retrieving analysis requests with status {Status}")]
    private static partial void LogRetrievingByStatus(ILogger logger, int status);

    [LoggerMessage(EventId = 106, Level = LogLevel.Information,
        Message = "Retrieving analysis requests for repository {RepositoryUrl}")]
    private static partial void LogRetrievingByRepository(ILogger logger, string repositoryUrl);

    [LoggerMessage(EventId = 107, Level = LogLevel.Information,
        Message = "Cancelling analysis request {RequestId}")]
    private static partial void LogCancellingAnalysisRequest(ILogger logger, Guid requestId);

    /// <summary>
    ///     Submit a new code analysis request
    /// </summary>
    [Authorize(Policy = "CodeAnalysis")]
    [EnableRateLimiting("llm-operations")]
    [HttpPost]
    [ProducesResponseType(typeof(SubmitAnalysisResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SubmitAnalysis(
        [FromBody] SubmitAnalysisRequest request,
        CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        LogSubmittingAnalysisRequest(logger, request.RepositoryUrl);

        // Serialize requirements list to JSON array string for domain entity
        string? requirementsJson = null;
        if (request.Requirements is { Count: > 0 })
        {
            var escaped = request.Requirements
                .Select(r => "\"" + r.Replace("\\", "\\\\", StringComparison.Ordinal)
                                     .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"");
            requirementsJson = "[" + string.Join(',', escaped) + "]";
        }

        var createResult = CodeAnalysisRequest.Create(
            Guid.NewGuid(),
            request.Title,
            request.Description,
            request.RepositoryUrl,
            request.MaxIterations,
            request.Type,
            request.FilePath,
            requirementsJson,
            request.TargetBranch,
            request.TargetCommit);

        if (createResult.IsFailure)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Validation Error",
                Detail = createResult.Error,
                Status = StatusCodes.Status400BadRequest
            });
        }

        var analysisRequest = createResult.Value;
        var saveResult = await codeAnalysisRepository.CreateAsync(analysisRequest, ct);

        if (saveResult.IsFailure)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails
            {
                Title = "Internal Error",
                Detail = saveResult.Error,
                Status = StatusCodes.Status500InternalServerError
            });
        }

        return CreatedAtAction(
            nameof(GetAnalysisStatus),
            new { requestId = analysisRequest.Id },
            new SubmitAnalysisResponse(analysisRequest.Id));
    }

    /// <summary>
    ///     Get the status of an analysis request
    /// </summary>
    [Authorize(Policy = "CodeAnalysisRead")]
    [HttpGet("{requestId:guid}")]
    [ProducesResponseType(typeof(AnalysisStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAnalysisStatus(
        Guid requestId,
        CancellationToken ct)
    {
        LogRetrievingAnalysisStatus(logger, requestId);

        var result = await codeAnalysisRepository.GetByIdAsync(requestId, ct);

        if (result.IsFailure)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Not Found",
                Detail = result.Error,
                Status = StatusCodes.Status404NotFound
            });
        }

        var entity = result.Value;
        var requestDto = MapToDto(entity);
        var iterations = entity.Iterations
            .Select(MapIterationToDto)
            .ToList()
            .AsReadOnly();

        return Ok(new AnalysisStatusDto(
            requestDto,
            entity.LastPromptSent ?? string.Empty,
            iterations));
    }

    /// <summary>
    ///     Submit AI response for the next iteration
    /// </summary>
    [Authorize(Policy = "CodeAnalysis")]
    [EnableRateLimiting("llm-operations")]
    [HttpPost("{requestId:guid}/iterate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> IterateAnalysis(
        Guid requestId,
        [FromBody] IterateAnalysisRequest request,
        CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        LogProcessingIteration(logger, requestId);

        // Fetch the current entity to determine iteration state
        var getResult = await codeAnalysisRepository.GetByIdAsync(requestId, ct);
        if (getResult.IsFailure)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Not Found",
                Detail = getResult.Error,
                Status = StatusCodes.Status404NotFound
            });
        }

        var entity = getResult.Value;
        var nextIteration = entity.CurrentIteration + 1;

        var iterateResult = await codeAnalysisRepository.UpdateIterationAsync(
            requestId,
            nextIteration,
            entity.LastPromptSent ?? "Initial prompt",
            request.AiResponse,
            ct);

        if (iterateResult.IsFailure)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Iteration Error",
                Detail = iterateResult.Error,
                Status = StatusCodes.Status400BadRequest
            });
        }

        return Ok(new { requestId, iteration = nextIteration });
    }

    /// <summary>
    ///     Finalize the analysis and optionally create a pull request
    /// </summary>
    [Authorize(Policy = "CodeAnalysis")]
    [EnableRateLimiting("llm-operations")]
    [HttpPost("{requestId:guid}/finalize")]
    [ProducesResponseType(typeof(FinalizeAnalysisResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> FinalizeAnalysis(
        Guid requestId,
        [FromBody] FinalizeAnalysisRequest request,
        CancellationToken ct)
    {
        LogFinalizingAnalysisRequest(logger, requestId);

        var completeResult = await codeAnalysisRepository.CompleteAsync(
            requestId,
            prUrl: null,
            finalCommitSha: null,
            ct);

        if (completeResult.IsFailure)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Finalization Error",
                Detail = completeResult.Error,
                Status = StatusCodes.Status400BadRequest
            });
        }

        // Fetch the completed entity to return the PR URL if available
        var getResult = await codeAnalysisRepository.GetByIdAsync(requestId, ct);
        var prUrl = getResult.IsSuccess ? getResult.Value.PullRequestUrl : null;

        return Ok(new FinalizeAnalysisResponse(prUrl));
    }

    /// <summary>
    ///     Cancel an analysis request
    /// </summary>
    [Authorize(Policy = "CodeAnalysis")]
    [HttpPost("{requestId:guid}/cancel")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CancelAnalysis(
        Guid requestId,
        CancellationToken ct)
    {
        LogCancellingAnalysisRequest(logger, requestId);

        var cancelResult = await codeAnalysisRepository.CancelAsync(requestId, ct);

        if (cancelResult.IsFailure)
        {
            if (cancelResult.Error.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                return NotFound(new ProblemDetails
                {
                    Title = "Not Found",
                    Detail = cancelResult.Error,
                    Status = StatusCodes.Status404NotFound
                });
            }

            return BadRequest(new ProblemDetails
            {
                Title = "Cancellation Error",
                Detail = cancelResult.Error,
                Status = StatusCodes.Status400BadRequest
            });
        }

        return Ok(new { requestId, status = "Cancelled" });
    }

    /// <summary>
    ///     Get next pending analysis request
    /// </summary>
    [Authorize(Policy = "CodeAnalysisRead")]
    [HttpGet("next-pending")]
    [ProducesResponseType(typeof(CodeAnalysisRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> GetNextPending(CancellationToken ct)
    {
        LogRetrievingNextPending(logger);

        var result = await codeAnalysisRepository.GetPendingAsync(1, ct);

        if (result.IsFailure)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails
            {
                Title = "Internal Error",
                Detail = result.Error,
                Status = StatusCodes.Status500InternalServerError
            });
        }

        var pending = result.Value;
        if (pending.Count == 0)
        {
            return NoContent();
        }

        return Ok(MapToDto(pending[0]));
    }

    /// <summary>
    ///     List analysis requests by status
    /// </summary>
    [Authorize(Policy = "CodeAnalysisRead")]
    [HttpGet("by-status/{status:int}")]
    [ProducesResponseType(typeof(IReadOnlyList<CodeAnalysisRequestDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetByStatus(
        int status,
        CancellationToken ct)
    {
        LogRetrievingByStatus(logger, status);

        if (!Enum.IsDefined(typeof(AnalysisStatus), status))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid Status",
                Detail = $"Invalid status value: {status}",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var result = await codeAnalysisRepository.GetByStatusAsync((AnalysisStatus)status, ct);

        if (result.IsFailure)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails
            {
                Title = "Internal Error",
                Detail = result.Error,
                Status = StatusCodes.Status500InternalServerError
            });
        }

        var dtos = result.Value.Select(MapToDto).ToList().AsReadOnly();
        return Ok(dtos);
    }

    /// <summary>
    ///     List analysis requests for a specific repository
    /// </summary>
    [Authorize(Policy = "CodeAnalysisRead")]
    [HttpGet("by-repository")]
    [ProducesResponseType(typeof(IReadOnlyList<CodeAnalysisRequestDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetByRepository(
        [FromQuery] string repositoryUrl,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Validation Error",
                Detail = "Repository URL is required",
                Status = StatusCodes.Status400BadRequest
            });
        }

        LogRetrievingByRepository(logger, repositoryUrl);

        var result = await codeAnalysisRepository.GetByRepositoryAsync(repositoryUrl, ct);

        if (result.IsFailure)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails
            {
                Title = "Internal Error",
                Detail = result.Error,
                Status = StatusCodes.Status500InternalServerError
            });
        }

        var dtos = result.Value.Select(MapToDto).ToList().AsReadOnly();
        return Ok(dtos);
    }

    /// <summary>
    ///     Maps a CodeAnalysisRequest entity to a CodeAnalysisRequestDto.
    /// </summary>
    private static CodeAnalysisRequestDto MapToDto(CodeAnalysisRequest entity) =>
        new(
            entity.Id,
            entity.Title,
            entity.Description,
            entity.RepositoryUrl,
            entity.FilePath,
            entity.Type,
            entity.Status,
            entity.CurrentIteration,
            entity.MaxIterations,
            entity.CreatedAt,
            entity.StartedAt,
            entity.CompletedAt);

    /// <summary>
    ///     Maps an AnalysisIteration entity to an AnalysisIterationDto.
    /// </summary>
    private static AnalysisIterationDto MapIterationToDto(AnalysisIteration iteration) =>
        new(
            iteration.IterationNumber,
            iteration.PromptSent,
            iteration.AiResponse,
            iteration.CompilationSucceeded ?? false,
            iteration.CreatedAt,
            null);
}
