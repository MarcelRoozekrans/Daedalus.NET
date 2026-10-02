using Daedalus.Agents;
using Daedalus.Agents.Workflow;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Daedalus.Api.Controllers;

/// <summary>
///     Answers 503 problem details, with no <c>Retry-After</c>, for every action of the controller it marks when
///     <c>Thalos:Workflow:Enabled</c> is <see langword="false"/>, before the controller is created.
/// </summary>
/// <remarks>
///     <para>
///     <b>Why a resource filter.</b> <see cref="WorkflowRunsController"/> takes <see cref="WorkflowRunGateway"/>, which
///     only an enabled host registers, so on a disabled host MVC cannot create the controller and every action fails
///     with a bare 500. MVC creates the controller after resource filters and before action filters, so a resource
///     filter is the one place that can answer instead, and it runs after authorization: an anonymous or unauthorized
///     caller still gets 401 or 403 and never learns the host's configuration from a 503.
///     </para>
///     <para>
///     The gateway stays a required dependency, so an enabled host that is missing it still fails loudly. The setting
///     is read from <see cref="WorkflowConfig.Enabled"/>, which every host registers, and not inferred from a missing
///     service. The wording is <see cref="DisabledManufactureRunStarter.DisabledMessage"/>, the one the starter uses.
///     </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class WorkflowEngineEnabledAttribute() : TypeFilterAttribute(typeof(WorkflowEngineEnabledFilter))
{
    private sealed class WorkflowEngineEnabledFilter(WorkflowConfig config) : IAsyncResourceFilter
    {
        public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
        {
            if (config.Enabled)
            {
                await next();
                return;
            }

            var problem = context.HttpContext.RequestServices.GetRequiredService<ProblemDetailsFactory>().CreateProblemDetails(
                context.HttpContext, StatusCodes.Status503ServiceUnavailable, detail: DisabledManufactureRunStarter.DisabledMessage);
            context.Result = new ObjectResult(problem) { StatusCode = StatusCodes.Status503ServiceUnavailable };
        }
    }
}
