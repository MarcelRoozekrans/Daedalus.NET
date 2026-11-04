using Daedalus.Agents;
using Daedalus.Agents.Workflow;
using Daedalus.Api.Endpoints;
using Microsoft.AspNetCore.Http;
using static Daedalus.Tests.Unit.Endpoints.EndpointTestContext;

namespace Daedalus.Tests.Unit.Endpoints;

/// <summary>
///     <see cref="WorkflowEngineEnabledEndpointFilter"/> answers what MVC's resource filter on the workflow-runs
///     controller answered: <c>ProblemDetailsFactory.CreateProblemDetails(503, detail: DisabledMessage)</c>, as the
///     <c>application/json</c> of <see cref="WorkflowRunsEndpoints"/>' group. The literal members are pinned since MVC
///     left the host; the case that compared them with MVC's factory went with it (R54).
/// </summary>
public sealed class WorkflowEngineEnabledEndpointFilterTests
{
    [Fact]
    public async Task Engine_disabled_answers_503_problem()
    {
        await using var services = Services();
        var context = Create(services, ProblemContentType.Json);
        var called = false;
        var filter = new WorkflowEngineEnabledEndpointFilter(new WorkflowConfig { Enabled = false });

        var result = await filter.InvokeAsync(new DefaultEndpointFilterInvocationContext(context), _ =>
        {
            called = true;
            return ValueTask.FromResult<object?>(null);
        });
        var text = await ExecuteAsync((IResult)result!, context);

        // Red: answer when Enabled is true instead of false; the disabled host then reaches next.
        called.Should().BeFalse();
        // Red: StatusCodes.Status500InternalServerError in the filter.
        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        context.Response.ContentType.Should().Be(Json);
        // Red: a detail other than DisabledManufactureRunStarter.DisabledMessage, or a title set by the filter.
        AssertProblem(text, ServiceUnavailableType, "Service Unavailable", 503, detail: DisabledManufactureRunStarter.DisabledMessage);
    }

    [Fact]
    public async Task Engine_enabled_calls_next()
    {
        await using var services = Services();
        var context = Create(services);
        var filter = new WorkflowEngineEnabledEndpointFilter(new WorkflowConfig { Enabled = true });

        var result = await filter.InvokeAsync(new DefaultEndpointFilterInvocationContext(context),
            _ => ValueTask.FromResult<object?>("next"));

        // Red: answer the 503 whatever the setting.
        result.Should().Be("next");
    }
}
