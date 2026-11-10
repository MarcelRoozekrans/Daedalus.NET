using System.ComponentModel.DataAnnotations;
using Daedalus.Application.DTOs;
using Daedalus.Application.Services;
using Daedalus.Application.Services.CodeAnalysis;
using Daedalus.Domain.CodeAnalysis;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Daedalus.Api.Endpoints;

/// <summary>API endpoints for code analysis and its iterative analysis orchestration (issue #279).</summary>
/// <remarks>
///     Ported from <c>CodeAnalysisController</c> on the pattern of <see cref="BrainstormEndpoints"/> (R42, R45). The
///     controller wrote every failure as an explicit <c>ProblemDetails</c> with its own title, which MVC's factory
///     never touches, so each goes out through <see cref="ApiProblem.AsGiven"/> (R43): no <c>type</c>, no
///     <c>traceId</c>, <c>application/json</c> from the group's <see cref="ProblemContentType.Json"/>. The two
///     anonymous success bodies of iterate and cancel became <see cref="IterateAnalysisResponse"/> and
///     <see cref="CancelAnalysisResponse"/>, written through <see cref="ApiJson.Of"/> so the endpoint declares a
///     bodiless 200, as MVC's <c>[ProducesResponseType(200)]</c> did (R52). The controller had no catch-all, so
///     neither do the handlers (R47). The two <c>ModelState.IsValid</c> checks are dropped (R10), and the blank
///     repository URL check became <c>[Required]</c> on its parameter (R52). A write whose guarded save lost a race to
///     another write, iterate, finalize or cancel, answers 409 with the refusal as its detail, matched by
///     <see cref="SaveConflict.IsConflict"/>, as projects and brainstorm sessions do (R95).
/// </remarks>
public static partial class CodeAnalysisEndpoints
{
    /// <summary>Maps <c>api/codeanalysis</c>, authenticated, with a policy per route and a rate limit on three.</summary>
    public static RouteGroupBuilder MapCodeAnalysisEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapApiGroup("api/codeanalysis")
            .RequireAuthorization()
            .WithTags("CodeAnalysis")
            .WithMetadata(ProblemContentType.Json);

        group.MapPost(string.Empty, SubmitAnalysis)
            .RequireAuthorization("CodeAnalysis")
            .RequireRateLimiting("llm-operations")
            .ProducesProblem(StatusCodes.Status400BadRequest, "application/json");

        group.MapGet("{requestId:guid}", GetAnalysisStatus)
            .RequireAuthorization("CodeAnalysisRead")
            .ProducesProblem(StatusCodes.Status404NotFound, "application/json");

        group.MapPost("{requestId:guid}/iterate", IterateAnalysis)
            .RequireAuthorization("CodeAnalysis")
            .RequireRateLimiting("llm-operations")
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest, "application/json")
            .ProducesProblem(StatusCodes.Status404NotFound, "application/json")
            .ProducesProblem(StatusCodes.Status409Conflict, "application/json");

        group.MapPost("{requestId:guid}/finalize", FinalizeAnalysis)
            .RequireAuthorization("CodeAnalysis")
            .RequireRateLimiting("llm-operations")
            .ProducesProblem(StatusCodes.Status400BadRequest, "application/json")
            .ProducesProblem(StatusCodes.Status404NotFound, "application/json")
            .ProducesProblem(StatusCodes.Status409Conflict, "application/json");

        group.MapPost("{requestId:guid}/cancel", CancelAnalysis)
            .RequireAuthorization("CodeAnalysis")
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest, "application/json")
            .ProducesProblem(StatusCodes.Status404NotFound, "application/json")
            .ProducesProblem(StatusCodes.Status409Conflict, "application/json");

        group.MapGet("next-pending", GetNextPending)
            .RequireAuthorization("CodeAnalysisRead");

        group.MapGet("by-status/{status:int}", GetByStatus)
            .RequireAuthorization("CodeAnalysisRead")
            .ProducesProblem(StatusCodes.Status400BadRequest, "application/json");

        group.MapGet("by-repository", GetByRepository)
            .RequireAuthorization("CodeAnalysisRead")
            .WithQueryParameter<string>("repositoryUrl")
            .ProducesProblem(StatusCodes.Status400BadRequest, "application/json");

        return group;
    }

    /// <summary>Submit a new code analysis request.</summary>
    /// <remarks>
    ///     An empty requirements list is passed on as <see langword="null"/>, which the <c>jsonb</c> column refuses,
    ///     so that request answers 500 today. The port keeps it, with no contract change; the fix is #428.
    /// </remarks>
    public static async Task<Results<Created<SubmitAnalysisResponse>, ApiProblemResult>> SubmitAnalysis(
        ApiBody<SubmitAnalysisRequest> request, HttpContext context, [FromServices] ICodeAnalysisRepository codeAnalysisRepository,
        [FromServices] ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var body = request.Value!;
        var logger = loggerFactory.CreateLogger(typeof(CodeAnalysisEndpoints));
        Log.SubmittingAnalysisRequest(logger, body.RepositoryUrl);

        // Serialize requirements list to JSON array string for domain entity
        string? requirementsJson = null;
        if (body.Requirements is { Count: > 0 })
        {
            var escaped = body.Requirements
                .Select(r => "\"" + r.Replace("\\", "\\\\", StringComparison.Ordinal)
                                     .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"");
            requirementsJson = "[" + string.Join(',', escaped) + "]";
        }

        var createResult = CodeAnalysisRequest.Create(
            Guid.NewGuid(),
            body.Title,
            body.Description,
            body.RepositoryUrl,
            body.MaxIterations,
            body.Type,
            body.FilePath,
            requirementsJson,
            body.TargetBranch,
            body.TargetCommit);

        if (createResult.IsFailure)
        {
            return Problem(context, StatusCodes.Status400BadRequest, "Validation Error", createResult.Error);
        }

        var analysisRequest = createResult.Value;
        var saveResult = await codeAnalysisRepository.CreateAsync(analysisRequest, ct);

        if (saveResult.IsFailure)
        {
            return Problem(context, StatusCodes.Status500InternalServerError, "Internal Error", saveResult.Error);
        }

        return TypedResults.Created(
            ApiLocation.For(context.Request, $"/api/CodeAnalysis/{analysisRequest.Id}"),
            new SubmitAnalysisResponse(analysisRequest.Id));
    }

    /// <summary>Get the status of an analysis request.</summary>
    public static async Task<Results<Ok<AnalysisStatusDto>, ApiProblemResult>> GetAnalysisStatus(
        Guid requestId, HttpContext context, [FromServices] ICodeAnalysisRepository codeAnalysisRepository,
        [FromServices] ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(typeof(CodeAnalysisEndpoints));
        Log.RetrievingAnalysisStatus(logger, requestId);

        var result = await codeAnalysisRepository.GetByIdAsync(requestId, ct);

        if (result.IsFailure)
        {
            return Problem(context, StatusCodes.Status404NotFound, "Not Found", result.Error);
        }

        var entity = result.Value;
        var requestDto = MapToDto(entity);
        var iterations = entity.Iterations
            .Select(MapIterationToDto)
            .ToList()
            .AsReadOnly();

        return TypedResults.Ok(new AnalysisStatusDto(
            requestDto,
            entity.LastPromptSent ?? string.Empty,
            iterations));
    }

    /// <summary>Submit AI response for the next iteration.</summary>
    public static async Task<Results<ApiJsonResult<IterateAnalysisResponse>, ApiProblemResult>> IterateAnalysis(
        Guid requestId, ApiBody<IterateAnalysisRequest> request, HttpContext context,
        [FromServices] ICodeAnalysisRepository codeAnalysisRepository, [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(typeof(CodeAnalysisEndpoints));
        Log.ProcessingIteration(logger, requestId);

        // Fetch the current entity to determine iteration state
        var getResult = await codeAnalysisRepository.GetByIdAsync(requestId, ct);
        if (getResult.IsFailure)
        {
            return Problem(context, StatusCodes.Status404NotFound, "Not Found", getResult.Error);
        }

        var entity = getResult.Value;
        var nextIteration = entity.CurrentIteration + 1;

        var iterateResult = await codeAnalysisRepository.UpdateIterationAsync(
            requestId,
            nextIteration,
            entity.LastPromptSent ?? "Initial prompt",
            request.Value!.AiResponse,
            ct);

        if (iterateResult.IsFailure)
        {
            return WriteFailure(context, "Iteration Error", iterateResult.Error);
        }

        return ApiJson.Of(new IterateAnalysisResponse(requestId, nextIteration), StatusCodes.Status200OK);
    }

    /// <summary>Finalize the analysis and optionally create a pull request.</summary>
    public static async Task<Results<Ok<FinalizeAnalysisResponse>, ApiProblemResult>> FinalizeAnalysis(
        Guid requestId, ApiBody<FinalizeAnalysisRequest> request, HttpContext context,
        [FromServices] ICodeAnalysisRepository codeAnalysisRepository, [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        // Bound so the body's 415, empty-body and implicit-required answers survive; its content is not read.
        _ = request;
        var logger = loggerFactory.CreateLogger(typeof(CodeAnalysisEndpoints));
        Log.FinalizingAnalysisRequest(logger, requestId);

        var completeResult = await codeAnalysisRepository.CompleteAsync(
            requestId,
            prUrl: null,
            finalCommitSha: null,
            ct);

        if (completeResult.IsFailure)
        {
            return WriteFailure(context, "Finalization Error", completeResult.Error);
        }

        // Fetch the completed entity to return the PR URL if available
        var getResult = await codeAnalysisRepository.GetByIdAsync(requestId, ct);
        var prUrl = getResult.IsSuccess ? getResult.Value.PullRequestUrl : null;

        return TypedResults.Ok(new FinalizeAnalysisResponse(prUrl));
    }

    /// <summary>Cancel an analysis request.</summary>
    public static async Task<Results<ApiJsonResult<CancelAnalysisResponse>, ApiProblemResult>> CancelAnalysis(
        Guid requestId, HttpContext context, [FromServices] ICodeAnalysisRepository codeAnalysisRepository,
        [FromServices] ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(typeof(CodeAnalysisEndpoints));
        Log.CancellingAnalysisRequest(logger, requestId);

        var cancelResult = await codeAnalysisRepository.CancelAsync(requestId, ct);

        if (cancelResult.IsFailure)
        {
            if (SaveConflict.IsConflict(cancelResult.Error))
            {
                return Problem(context, StatusCodes.Status409Conflict, "Conflict", cancelResult.Error);
            }

            if (cancelResult.Error.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                return Problem(context, StatusCodes.Status404NotFound, "Not Found", cancelResult.Error);
            }

            return Problem(context, StatusCodes.Status400BadRequest, "Cancellation Error", cancelResult.Error);
        }

        return ApiJson.Of(new CancelAnalysisResponse(requestId, "Cancelled"), StatusCodes.Status200OK);
    }

    /// <summary>Get next pending analysis request.</summary>
    public static async Task<Results<Ok<CodeAnalysisRequestDto>, NoContent, ApiProblemResult>> GetNextPending(
        HttpContext context, [FromServices] ICodeAnalysisRepository codeAnalysisRepository,
        [FromServices] ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(typeof(CodeAnalysisEndpoints));
        Log.RetrievingNextPending(logger);

        var result = await codeAnalysisRepository.GetPendingAsync(1, ct);

        if (result.IsFailure)
        {
            return Problem(context, StatusCodes.Status500InternalServerError, "Internal Error", result.Error);
        }

        var pending = result.Value;
        if (pending.Count == 0)
        {
            return TypedResults.NoContent();
        }

        return TypedResults.Ok(MapToDto(pending[0]));
    }

    /// <summary>List analysis requests by status.</summary>
    public static async Task<Results<Ok<IReadOnlyList<CodeAnalysisRequestDto>>, ApiProblemResult>> GetByStatus(
        int status, HttpContext context, [FromServices] ICodeAnalysisRepository codeAnalysisRepository,
        [FromServices] ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(typeof(CodeAnalysisEndpoints));
        Log.RetrievingByStatus(logger, status);

        if (!Enum.IsDefined(typeof(AnalysisStatus), status))
        {
            return Problem(context, StatusCodes.Status400BadRequest, "Invalid Status", $"Invalid status value: {status}");
        }

        var result = await codeAnalysisRepository.GetByStatusAsync((AnalysisStatus)status, ct);

        if (result.IsFailure)
        {
            return Problem(context, StatusCodes.Status500InternalServerError, "Internal Error", result.Error);
        }

        IReadOnlyList<CodeAnalysisRequestDto> dtos = result.Value.Select(MapToDto).ToList();
        return TypedResults.Ok(dtos);
    }

    /// <summary>List analysis requests for a specific repository.</summary>
    public static async Task<Results<Ok<IReadOnlyList<CodeAnalysisRequestDto>>, ApiProblemResult>> GetByRepository(
        [Required] ApiQuery<string> repositoryUrl, HttpContext context,
        [FromServices] ICodeAnalysisRepository codeAnalysisRepository, [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(typeof(CodeAnalysisEndpoints));
        Log.RetrievingByRepository(logger, repositoryUrl.Value);

        var result = await codeAnalysisRepository.GetByRepositoryAsync(repositoryUrl.Value, ct);

        if (result.IsFailure)
        {
            return Problem(context, StatusCodes.Status500InternalServerError, "Internal Error", result.Error);
        }

        IReadOnlyList<CodeAnalysisRequestDto> dtos = result.Value.Select(MapToDto).ToList();
        return TypedResults.Ok(dtos);
    }

    /// <summary>
    ///     The answer to a refused write: 409 Conflict for a save that lost a race to another write, else 400 with
    ///     <paramref name="title"/>; the refusal is the detail either way. A failure without a text, which only a test
    ///     double gives, answers 400, as before.
    /// </summary>
    private static ApiProblemResult WriteFailure(HttpContext context, string title, string? error) =>
        error is not null && SaveConflict.IsConflict(error)
            ? Problem(context, StatusCodes.Status409Conflict, "Conflict", error)
            : Problem(context, StatusCodes.Status400BadRequest, title, error);

    private static ApiProblemResult Problem(HttpContext context, int status, string title, string? detail) =>
        ApiProblem.AsGiven(context, new ProblemDetails { Title = title, Detail = detail, Status = status });

    /// <summary>Maps a CodeAnalysisRequest entity to a CodeAnalysisRequestDto.</summary>
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

    /// <summary>Maps an AnalysisIteration entity to an AnalysisIterationDto.</summary>
    private static AnalysisIterationDto MapIterationToDto(AnalysisIteration iteration) =>
        new(
            iteration.IterationNumber,
            iteration.PromptSent,
            iteration.AiResponse,
            iteration.CompilationSucceeded ?? false,
            iteration.CreatedAt,
            null);

    private static partial class Log
    {
        [LoggerMessage(EventId = 100, Level = LogLevel.Information,
            Message = "Submitting analysis request for repository {RepositoryUrl}")]
        public static partial void SubmittingAnalysisRequest(ILogger logger, string repositoryUrl);

        [LoggerMessage(EventId = 101, Level = LogLevel.Information,
            Message = "Retrieving analysis status for request {RequestId}")]
        public static partial void RetrievingAnalysisStatus(ILogger logger, Guid requestId);

        [LoggerMessage(EventId = 102, Level = LogLevel.Information,
            Message = "Processing iteration for analysis request {RequestId}")]
        public static partial void ProcessingIteration(ILogger logger, Guid requestId);

        [LoggerMessage(EventId = 103, Level = LogLevel.Information, Message = "Finalizing analysis request {RequestId}")]
        public static partial void FinalizingAnalysisRequest(ILogger logger, Guid requestId);

        [LoggerMessage(EventId = 104, Level = LogLevel.Information, Message = "Retrieving next pending analysis request")]
        public static partial void RetrievingNextPending(ILogger logger);

        [LoggerMessage(EventId = 105, Level = LogLevel.Information,
            Message = "Retrieving analysis requests with status {Status}")]
        public static partial void RetrievingByStatus(ILogger logger, int status);

        [LoggerMessage(EventId = 106, Level = LogLevel.Information,
            Message = "Retrieving analysis requests for repository {RepositoryUrl}")]
        public static partial void RetrievingByRepository(ILogger logger, string repositoryUrl);

        [LoggerMessage(EventId = 107, Level = LogLevel.Information,
            Message = "Cancelling analysis request {RequestId}")]
        public static partial void CancellingAnalysisRequest(ILogger logger, Guid requestId);
    }
}
