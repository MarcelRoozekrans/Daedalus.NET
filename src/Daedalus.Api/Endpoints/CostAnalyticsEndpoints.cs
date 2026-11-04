using System.ComponentModel.DataAnnotations;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Daedalus.Api.Endpoints;

/// <summary>Cost analytics and estimation: four reads over <see cref="ICostAnalyticsService"/> and one estimate.</summary>
/// <remarks>
///     Ported from <c>CostAnalyticsController</c> on the pattern of <see cref="TaskExecutionEndpoints"/> (R42). The
///     controller carried a bare <c>[Authorize]</c> and the <c>TaskRead</c> policy on every action, so the group
///     requires an authenticated user and each route adds the policy. The estimate answers an explicit
///     <c>ProblemDetails</c> as the controller wrote it and a validation problem for a missing <c>modelId</c>, which
///     MVC's implicit required check refused (R43).
/// </remarks>
public static partial class CostAnalyticsEndpoints
{
    private const int DefaultMaxIterations = 10;
    private const int DefaultEstimatedPromptTokens = 4000;

    /// <summary>Maps <c>api/cost-analytics</c>, authenticated, with policy <c>TaskRead</c> on every route.</summary>
    public static RouteGroupBuilder MapCostAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapApiGroup("api/cost-analytics")
            .RequireAuthorization()
            .WithTags("CostAnalytics")
            .WithMetadata(ProblemContentType.Json);

        group.MapGet("summary", GetSummary)
            .RequireAuthorization("TaskRead");

        group.MapGet("by-project", GetByProject)
            .RequireAuthorization("TaskRead");

        group.MapGet("by-project/{id:guid}", GetByProjectId)
            .RequireAuthorization("TaskRead");

        group.MapGet("estimate", EstimateCost)
            .RequireAuthorization("TaskRead")
            .ProducesProblem(StatusCodes.Status404NotFound, "application/json")
            .WithQueryParameter<string>("modelId")
            .WithQueryParameter("maxIterations", DefaultMaxIterations)
            .WithQueryParameter("estimatedPromptTokens", DefaultEstimatedPromptTokens);

        group.MapGet("pricing", GetPricing)
            .RequireAuthorization("TaskRead");

        return group;
    }

    /// <summary>Get overall cost summary.</summary>
    public static async Task<Results<Ok<CostSummaryDto>, ApiJsonResult<ErrorBody>>> GetSummary(
        [FromServices] ICostAnalyticsService costService, [FromServices] ILoggerFactory loggerFactory, CancellationToken ct)
    {
        try
        {
            return TypedResults.Ok(await costService.GetSummaryAsync(ct));
        }
        catch (Exception ex)
        {
            Log.ErrorRetrievingCosts(loggerFactory.CreateLogger(typeof(CostAnalyticsEndpoints)), ex);
            return InternalServerError();
        }
    }

    /// <summary>Get cost breakdown by project.</summary>
    public static async Task<Results<Ok<IReadOnlyList<ProjectCostDto>>, ApiJsonResult<ErrorBody>>> GetByProject(
        [FromServices] ICostAnalyticsService costService, [FromServices] ILoggerFactory loggerFactory, CancellationToken ct)
    {
        try
        {
            return TypedResults.Ok(await costService.GetCostsByProjectAsync(ct));
        }
        catch (Exception ex)
        {
            Log.ErrorRetrievingCosts(loggerFactory.CreateLogger(typeof(CostAnalyticsEndpoints)), ex);
            return InternalServerError();
        }
    }

    /// <summary>Get per-task cost breakdown for a project.</summary>
    public static async Task<Results<Ok<IReadOnlyList<TaskCostDto>>, ApiJsonResult<ErrorBody>>> GetByProjectId(
        Guid id, [FromServices] ICostAnalyticsService costService, [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        try
        {
            return TypedResults.Ok(await costService.GetCostsByProjectIdAsync(id, ct));
        }
        catch (Exception ex)
        {
            Log.ErrorRetrievingCosts(loggerFactory.CreateLogger(typeof(CostAnalyticsEndpoints)), ex);
            return InternalServerError();
        }
    }

    /// <summary>Estimate the cost of a planned run of up to <c>maxIterations</c> turns.</summary>
    /// <remarks>
    ///     The union adds no metadata beyond the 200: <see cref="ApiProblemResult"/> and <see cref="ApiJsonResult{T}"/>
    ///     declare nothing, so the 404 comes from <c>ProducesProblem</c> and the snapshot stays the controller's (R43).
    ///     A missing or blank <c>modelId</c> is refused while binding, from <c>[Required]</c> (R43).
    /// </remarks>
    public static async Task<Results<Ok<CostEstimateDto>, ApiProblemResult, ApiJsonResult<ErrorBody>>> EstimateCost(
        [Required] ApiQuery<string> modelId, ApiQuery<int> maxIterations, ApiQuery<int> estimatedPromptTokens, HttpContext context,
        [FromServices] ICostAnalyticsService costService, [FromServices] ILoggerFactory loggerFactory, CancellationToken ct)
    {
        try
        {
            var result = await costService.EstimateCostAsync(
                modelId.Value,
                maxIterations.ValueOr(DefaultMaxIterations),
                estimatedPromptTokens.ValueOr(DefaultEstimatedPromptTokens),
                ct);

            if (result.IsFailure)
            {
                return ApiProblem.AsGiven(context, new ProblemDetails
                {
                    Title = "Not Found",
                    Detail = result.Error,
                    Status = StatusCodes.Status404NotFound,
                });
            }

            return TypedResults.Ok(result.Value);
        }
        catch (Exception ex)
        {
            Log.ErrorRetrievingCosts(loggerFactory.CreateLogger(typeof(CostAnalyticsEndpoints)), ex);
            return InternalServerError();
        }
    }

    /// <summary>Get configured model pricing.</summary>
    public static async Task<Results<Ok<IReadOnlyList<ModelPricingDto>>, ApiJsonResult<ErrorBody>>> GetPricing(
        [FromServices] ICostAnalyticsService costService, [FromServices] ILoggerFactory loggerFactory, CancellationToken ct)
    {
        try
        {
            return TypedResults.Ok(await costService.GetPricingAsync(ct));
        }
        catch (Exception ex)
        {
            Log.ErrorRetrievingCosts(loggerFactory.CreateLogger(typeof(CostAnalyticsEndpoints)), ex);
            return InternalServerError();
        }
    }

    private static ApiJsonResult<ErrorBody> InternalServerError() =>
        ApiJson.Of(new ErrorBody("Internal server error"), StatusCodes.Status500InternalServerError);

    private static partial class Log
    {
        [LoggerMessage(EventId = 200, Level = LogLevel.Error, Message = "Error retrieving cost analytics")]
        public static partial void ErrorRetrievingCosts(ILogger logger, Exception ex);
    }
}
