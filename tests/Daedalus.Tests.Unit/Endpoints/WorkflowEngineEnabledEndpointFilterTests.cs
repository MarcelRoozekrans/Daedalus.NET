using Daedalus.Agents;
using Daedalus.Agents.Workflow;
using Daedalus.Api.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using static Daedalus.Tests.Unit.Endpoints.EndpointTestContext;

namespace Daedalus.Tests.Unit.Endpoints;

/// <summary>
///     <see cref="WorkflowEngineEnabledEndpointFilter"/> answers what <c>WorkflowEngineEnabledAttribute</c>'s resource
///     filter answers today: <c>ProblemDetailsFactory.CreateProblemDetails(503, detail: DisabledMessage)</c>, as
///     WorkflowRunsController's <c>application/json</c>.
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
    public async Task Engine_disabled_problem_equals_the_mvc_attributes()
    {
        await using var services = Services(collection => collection.AddMvcCore());
        var context = Create(services);
        var filter = new WorkflowEngineEnabledEndpointFilter(new WorkflowConfig { Enabled = false });

        var result = (ApiProblemResult)(await filter.InvokeAsync(new DefaultEndpointFilterInvocationContext(context),
            _ => ValueTask.FromResult<object?>(null)))!;
        var mvc = services.GetRequiredService<ProblemDetailsFactory>().CreateProblemDetails(
            context, StatusCodes.Status503ServiceUnavailable, detail: DisabledManufactureRunStarter.DisabledMessage);

        // Red: the 503 row of ApiProblem's table changed; the attribute's problem comes from MVC's factory.
        result.ProblemDetails.Should().BeEquivalentTo(mvc);
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
