using Daedalus.Agents;
using Daedalus.Agents.Workflow;

namespace Daedalus.Api.Endpoints;

/// <summary>
///     The endpoints' counterpart of <see cref="Controllers.WorkflowEngineEnabledAttribute"/>: when
///     <c>Thalos:Workflow:Enabled</c> is <see langword="false"/>, answers 503 with
///     <see cref="DisabledManufactureRunStarter.DisabledMessage"/> and no <c>Retry-After</c>, the problem MVC's
///     <c>ProblemDetailsFactory</c> builds for the attribute. Endpoint filters run after authorization, as the
///     attribute's resource filter does, so an unauthorized caller still gets 401 or 403.
/// </summary>
/// <remarks>
///     <see cref="DaedalusEndpoints.MapApiGroup"/> adds it ahead of <see cref="ZeroAllocValidationEndpointFilter"/>,
///     because MVC runs the resource filter before model binding: a disabled host answers 503 for a body that would
///     not bind as well.
/// </remarks>
internal sealed class WorkflowEngineEnabledEndpointFilter(WorkflowConfig config) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (config.Enabled)
        {
            return await next(context);
        }

        return ApiProblem.Of(context.HttpContext, StatusCodes.Status503ServiceUnavailable,
            detail: DisabledManufactureRunStarter.DisabledMessage);
    }
}
