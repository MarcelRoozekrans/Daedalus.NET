using Daedalus.Agents;
using Daedalus.Agents.Workflow;

namespace Daedalus.Api.Endpoints;

/// <summary>
///     Answers 503 problem details, with <see cref="DisabledManufactureRunStarter.DisabledMessage"/> and no
///     <c>Retry-After</c>, for every endpoint of a group mapped with <c>requiresWorkflowEngine</c> when
///     <c>Thalos:Workflow:Enabled</c> is <see langword="false"/>: the problem MVC's problem details factory built for
///     the resource filter it replaces. Endpoint filters run after authorization, so an anonymous or unauthorized caller
///     still gets 401 or 403 and never learns the host's configuration from a 503.
/// </summary>
/// <remarks>
///     <para>
///     <b>Why a filter.</b> <see cref="WorkflowRunsEndpoints"/> needs <see cref="WorkflowRunGateway"/>, which only an
///     enabled host registers, so its handlers resolve it from the request's services after this filter has let the
///     request through; a handler parameter would be resolved before the filter runs, and a disabled host would answer
///     a bare 500. The gateway stays required there, so an enabled host that is missing it still fails loudly. The
///     setting is read from <see cref="WorkflowConfig.Enabled"/>, which every host registers, and not inferred from a
///     missing service.
///     </para>
///     <para>
///     <see cref="DaedalusEndpoints.MapApiGroup"/> adds it ahead of <see cref="ZeroAllocValidationEndpointFilter"/>,
///     because MVC ran the resource filter before model binding: a disabled host answers 503 for a body that would not
///     bind as well.
///     </para>
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
