using System.Text.Json.Nodes;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs.Scheduling;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;

namespace Daedalus.Api.Endpoints;

/// <summary>
///     Answers "a digest did not arrive — where did it die?" over HTTP, and offers the one remedy the page
///     provides: resending an undelivered run's message. The two read routes are a thin seam onto
///     <see cref="IScheduleDiagnostics"/>: they call straight through and shape the response; every
///     classification decision belongs to the service, where it is tested. The resend route is the same shape
///     onto <see cref="IScheduleDeliveryActions"/> — the lookup from execution id to outbox row is service work,
///     not endpoint work.
/// </summary>
/// <remarks>
///     Ported from <c>SchedulesController</c> on the pattern of <see cref="AgentEndpoints"/> (R38). The reads and the one
///     write they enable belong on one resource per the reviewer's ruling, so they stay in one class.
///     The resend 400 is declared as <c>ProblemDetails</c> in metadata while the body is <see cref="ErrorBody"/>,
///     as the controller's attribute and code did (R41).
/// </remarks>
public static class SchedulesEndpoints
{
    /// <summary>The number of runs <see cref="GetRunHistory"/> returns when <c>take</c> is not sent.</summary>
    private const int DefaultTake = 20;

    /// <summary>Maps <c>api/schedules</c>, under policy <c>AgentUse</c>.</summary>
    public static RouteGroupBuilder MapSchedulesEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapApiGroup("api/schedules")
            .RequireAuthorization("AgentUse")
            .WithTags("Schedules")
            .WithMetadata(ProblemContentType.Json);

        group.MapGet(string.Empty, GetOverview);

        group.MapGet("{scheduleId:guid}/runs", GetRunHistory)
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                // A BindAsync parameter contributes no OpenAPI parameter (R30); MVC described take like this.
                operation.Parameters ??= [];
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = "take",
                    In = ParameterLocation.Query,
                    Schema = new OpenApiSchema
                    {
                        Pattern = "^-?(?:0|[1-9]\\d*)$",
                        Type = JsonSchemaType.Integer | JsonSchemaType.String,
                        Format = "int32",
                        Default = JsonValue.Create(DefaultTake),
                    },
                });
                return Task.CompletedTask;
            });

        group.MapPost("{scheduleId:guid}/runs/{executionId:guid}/resend", Resend)
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest, "application/json");

        return group;
    }

    /// <summary>Every schedule's current state and the verdict on its most recent run, including disabled ones.</summary>
    public static async Task<Ok<IReadOnlyList<RunDiagnosis>>> GetOverview(
        [FromServices] IScheduleDiagnostics diagnostics, CancellationToken ct) =>
        TypedResults.Ok(await diagnostics.GetOverviewAsync(ct));

    /// <summary>The most recent executions of one schedule, newest first.</summary>
    public static async Task<Ok<IReadOnlyList<RunDiagnosis>>> GetRunHistory(
        Guid scheduleId, ApiQuery<int> take, [FromServices] IScheduleDiagnostics diagnostics, CancellationToken ct) =>
        TypedResults.Ok(await diagnostics.GetRunHistoryAsync(scheduleId, take.ValueOr(DefaultTake), ct));

    /// <summary>
    ///     Requeues an undelivered run's dead-lettered message for redelivery. No confirmation step: requeueing
    ///     risks a duplicate digest, not data loss. Never rewrites a verdict — the caller re-resolves it from the
    ///     next <see cref="GetOverview"/> or <see cref="GetRunHistory"/> call, so the page never claims a
    ///     delivery it has not observed. <c>scheduleId</c> identifies the resource in the URL but is not needed
    ///     to resolve the outbox row, so it is bound and ignored.
    /// </summary>
    /// <remarks>
    ///     Returns <see cref="IResult"/> so the declared metadata stays the controller's, 200 and a
    ///     <c>ProblemDetails</c> 400, instead of the <see cref="ErrorBody"/> 400 a typed union would add (R41).
    /// </remarks>
    public static async Task<IResult> Resend(
        Guid scheduleId, Guid executionId, [FromServices] IScheduleDeliveryActions deliveryActions, CancellationToken ct)
    {
        // Bound so the route's guid constraint and the OpenAPI path parameter survive (R41); the row is found by execution.
        _ = scheduleId;
        var result = await deliveryActions.RequeueAsync(executionId, ct);
        return result.IsSuccess
            ? TypedResults.Ok()
            : TypedResults.BadRequest(new ErrorBody(result.Error));
    }
}
