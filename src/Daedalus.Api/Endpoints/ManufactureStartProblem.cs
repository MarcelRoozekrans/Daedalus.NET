using Daedalus.Agents.Workflow;

namespace Daedalus.Api.Endpoints;

/// <summary>
///     The one mapping of a <see cref="ManufactureStartFailure"/> to a response, used by <c>POST /api/workflow-runs</c> and
///     <c>POST /api/tasks/{id}/manufacture</c>, so the two cannot drift apart.
/// </summary>
internal static class ManufactureStartProblem
{
    /// <summary>The seconds a client is told to wait before retrying a start the host could not serve.</summary>
    public const string RetryAfterSeconds = "30";

    /// <summary>The problem for <paramref name="failure"/>, as <see cref="StatusOf"/> maps it, in the endpoint's content type.</summary>
    public static ApiProblemResult From(HttpContext context, ManufactureStartFailure failure)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(failure);

        return ApiProblem.Of(context, StatusOf(failure, context.Response), failure.Message);
    }

    /// <summary>
    ///     <c>Invalid</c> is 400, <c>Unstartable</c> 422, <c>Unavailable</c> 503 with <c>Retry-After: 30</c> set on
    ///     <paramref name="response"/>, <c>Disabled</c> 503 without it, and anything else 500. The failure's message is
    ///     the problem detail.
    /// </summary>
    private static int StatusOf(ManufactureStartFailure failure, HttpResponse response)
    {
        switch (failure.Kind)
        {
            case ManufactureStartFailureKind.Invalid:
                return StatusCodes.Status400BadRequest;
            case ManufactureStartFailureKind.Unstartable:
                return StatusCodes.Status422UnprocessableEntity;
            case ManufactureStartFailureKind.Unavailable:
                response.Headers.RetryAfter = RetryAfterSeconds;
                return StatusCodes.Status503ServiceUnavailable;
            case ManufactureStartFailureKind.Disabled:
                return StatusCodes.Status503ServiceUnavailable;
            default:
                return StatusCodes.Status500InternalServerError;
        }
    }
}
