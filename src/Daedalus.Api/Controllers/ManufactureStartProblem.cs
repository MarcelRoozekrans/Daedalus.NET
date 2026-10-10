using Daedalus.Agents.Workflow;
using Microsoft.AspNetCore.Mvc;

namespace Daedalus.Api.Controllers;

/// <summary>
///     The one mapping of a <see cref="ManufactureStartFailure"/> to a response, used by <c>POST /api/workflow-runs</c> and
///     <c>POST /api/tasks/{id}/manufacture</c>, so the two cannot drift apart.
/// </summary>
internal static class ManufactureStartProblem
{
    /// <summary>The seconds a client is told to wait before retrying a start the host could not serve.</summary>
    public const string RetryAfterSeconds = "30";

    /// <summary>
    ///     <c>Invalid</c> is 400, <c>Unstartable</c> 422, <c>Unavailable</c> 503 with <c>Retry-After: 30</c>, <c>Disabled</c>
    ///     503 without it, and anything else 500. The failure's message is the problem detail.
    /// </summary>
    public static IActionResult From(ControllerBase controller, ManufactureStartFailure failure)
    {
        switch (failure.Kind)
        {
            case ManufactureStartFailureKind.Invalid:
                return controller.Problem(detail: failure.Message, statusCode: StatusCodes.Status400BadRequest);
            case ManufactureStartFailureKind.Unstartable:
                return controller.Problem(detail: failure.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
            case ManufactureStartFailureKind.Unavailable:
                controller.Response.Headers.RetryAfter = RetryAfterSeconds;
                return controller.Problem(detail: failure.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            case ManufactureStartFailureKind.Disabled:
                return controller.Problem(detail: failure.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            default:
                return controller.Problem(detail: failure.Message, statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}
