using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Daedalus.Api.Endpoints;

/// <summary>PRD generation and conversion of PRD items to tasks, over <see cref="IPrdService"/>.</summary>
/// <remarks>
///     Ported from <c>PrdController</c> on the pattern of <see cref="CostAnalyticsEndpoints"/> (R42, R43). The group
///     requires an authenticated user and each route adds its policy and rate limit. The controller's catch-alls
///     answered <c>{ "error", "details" }</c>, not the bare <c>Internal server error</c> body, and still do (R44).
/// </remarks>
public static partial class PrdEndpoints
{
    /// <summary>Maps <c>api/prd</c>, authenticated, with a policy and a rate limit per route.</summary>
    public static RouteGroupBuilder MapPrdEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapApiGroup("api/prd")
            .RequireAuthorization()
            .WithTags("Prd")
            .WithMetadata(ProblemContentType.Json);

        group.MapPost("generate", GeneratePrd)
            .RequireAuthorization("CodeAnalysis")
            .RequireRateLimiting("llm-operations")
            .ProducesProblem(StatusCodes.Status400BadRequest, "application/json")
            .Produces(StatusCodes.Status500InternalServerError);

        group.MapPost("convert-to-tasks", ConvertToTasks)
            .RequireAuthorization("TaskManagement")
            .RequireRateLimiting("write-operations")
            .ProducesProblem(StatusCodes.Status400BadRequest, "application/json")
            .Produces(StatusCodes.Status500InternalServerError);

        return group;
    }

    /// <summary>Generates a PRD from user requirements using the LLM in PRD agent mode.</summary>
    public static async Task<
        Results<Ok<PrdResponseDto>, ApiJsonResult<ErrorBody>, ApiJsonResult<ErrorDetailBody>>> GeneratePrd(
        ApiBody<PrdRequestDto> request, [FromServices] IPrdService prdService, [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (request.Value is not { } body || string.IsNullOrWhiteSpace(body.UserRequirements))
        {
            return BadRequest("User requirements cannot be empty");
        }

        var logger = loggerFactory.CreateLogger(typeof(PrdEndpoints));
        try
        {
            Log.GeneratingPrd(logger, body.ProjectId);

            var result = await prdService.GeneratePrdAsync(body.ProjectId, body.UserRequirements, body.Context, ct);

            if (result.IsFailure)
            {
                Log.PrdGenerationFailed(logger, result.Error);
                return BadRequest(result.Error);
            }

            Log.PrdGeneratedSuccessfully(logger, result.Value.AllItems.Count);

            return TypedResults.Ok(result.Value);
        }
        catch (Exception ex)
        {
            Log.ErrorGeneratingPrd(logger, body.ProjectId, ex);
            return InternalServerError("Error generating PRD", ex);
        }
    }

    /// <summary>Converts selected PRD items to tasks and persists them to the database.</summary>
    public static async Task<
        Results<Ok<List<TaskDto>>, ApiJsonResult<ErrorBody>, ApiJsonResult<ErrorDetailBody>>> ConvertToTasks(
        ApiBody<PrdConversionRequestDto> request, [FromServices] IPrdService prdService,
        [FromServices] ILoggerFactory loggerFactory, CancellationToken ct)
    {
        if (request.Value is not { } body || body.PrdItems.Count == 0)
        {
            return BadRequest("No PRD items to convert");
        }

        var logger = loggerFactory.CreateLogger(typeof(PrdEndpoints));
        try
        {
            Log.ConvertingPrdItems(logger, body.PrdItems.Count, body.ProjectId);

            var result = await prdService.ConvertToTasksAsync(body.ProjectId, body.PrdItems, ct);

            if (result.IsFailure)
            {
                Log.ConversionFailed(logger, result.Error);
                return BadRequest(result.Error);
            }

            Log.ConversionSuccessful(logger, result.Value.Count);

            return TypedResults.Ok(result.Value);
        }
        catch (Exception ex)
        {
            Log.ErrorConvertingPrdItems(logger, body.ProjectId, ex);
            return InternalServerError("Error converting PRD items to tasks", ex);
        }
    }

    private static ApiJsonResult<ErrorBody> BadRequest(string error) =>
        ApiJson.Of(new ErrorBody(error), StatusCodes.Status400BadRequest);

    private static ApiJsonResult<ErrorDetailBody> InternalServerError(string error, Exception ex) =>
        ApiJson.Of(new ErrorDetailBody(error, ex.Message), StatusCodes.Status500InternalServerError);

    private static partial class Log
    {
        [LoggerMessage(EventId = 100, Level = LogLevel.Information, Message = "Generating PRD for project {ProjectId}")]
        public static partial void GeneratingPrd(ILogger logger, Guid projectId);

        [LoggerMessage(EventId = 101, Level = LogLevel.Warning, Message = "PRD generation failed: {Error}")]
        public static partial void PrdGenerationFailed(ILogger logger, string error);

        [LoggerMessage(EventId = 102, Level = LogLevel.Information,
            Message = "PRD generated successfully with {ItemCount} items")]
        public static partial void PrdGeneratedSuccessfully(ILogger logger, int itemCount);

        [LoggerMessage(EventId = 103, Level = LogLevel.Error, Message = "Error generating PRD for project {ProjectId}")]
        public static partial void ErrorGeneratingPrd(ILogger logger, Guid projectId, Exception ex);

        [LoggerMessage(EventId = 104, Level = LogLevel.Information,
            Message = "Converting {ItemCount} PRD items to tasks for project {ProjectId}")]
        public static partial void ConvertingPrdItems(ILogger logger, int itemCount, Guid projectId);

        [LoggerMessage(EventId = 105, Level = LogLevel.Warning, Message = "PRD to tasks conversion failed: {Error}")]
        public static partial void ConversionFailed(ILogger logger, string error);

        [LoggerMessage(EventId = 106, Level = LogLevel.Information,
            Message = "Successfully converted {ItemCount} PRD items to tasks")]
        public static partial void ConversionSuccessful(ILogger logger, int itemCount);

        [LoggerMessage(EventId = 107, Level = LogLevel.Error,
            Message = "Error converting PRD items to tasks for project {ProjectId}")]
        public static partial void ErrorConvertingPrdItems(ILogger logger, Guid projectId, Exception ex);
    }
}
