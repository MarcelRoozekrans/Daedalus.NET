using Daedalus.Agents.Workflow;
using Daedalus.Api.Endpoints;
using Daedalus.Api.Hubs;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Thalos;
using Thalos.Workflow;

namespace Daedalus.Tests.Integration.Workflow;

/// <summary>
///     <see cref="WorkflowRunsEndpoints"/> on a host with <c>Thalos:Workflow:Enabled=false</c>, which is
///     <see cref="ApiWebApplicationFactory"/>'s default. Such a host registers no <c>WorkflowRunGateway</c> and no
///     <c>IWorkflowRunQuery</c>, which the handlers resolve only after <see cref="WorkflowEngineEnabledEndpointFilter"/>
///     has let a request through; without the filter every route would fail resolving them with a bare 500.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class WorkflowDisabledHostTests(PostgresFixture fixture)
{
    /// <summary>
    ///     A write that starts a run, the list, a read, and three writes on a run all answer the same 503 problem
    ///     details naming the disabled engine, with no <c>Retry-After</c>, because no retry gets past a host setting.
    /// </summary>
    [Theory]
    [InlineData("POST", "/api/workflow-runs")]
    [InlineData("GET", "/api/workflow-runs")]
    [InlineData("GET", "/api/workflow-runs/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "/api/workflow-runs/00000000-0000-0000-0000-000000000001/resume")]
    [InlineData("POST", "/api/workflow-runs/00000000-0000-0000-0000-000000000001/cancel")]
    [InlineData("POST", "/api/workflow-runs/00000000-0000-0000-0000-000000000001/retry")]
    public async Task Every_action_of_a_disabled_host_is_a_503_without_retry_after(string method, string url)
    {
        await using var factory = new ApiWebApplicationFactory(fixture.ConnectionString, Substitute.For<IAgentRuntime>());
        using var client = Client(factory, "an-admin", "admin");
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (string.Equals(method, "POST", StringComparison.Ordinal))
        {
            request.Content = JsonContent.Create(new { workIntent = "Tighten a guard.", repository = "sandbox", signal = "human_approval" });
        }

        var response = await client.SendAsync(request);

        // Red: requiresWorkflowEngine: false in place of true on the group in MapWorkflowRunsEndpoints; every row but
        //      the start then reaches its handler or the validation filter and answers a 500 or a 400. The start row
        //      stays 503 by design: the host's DisabledManufactureRunStarter answers the same problem (R53).
        // Red: StatusCodes.Status500InternalServerError in place of Status503ServiceUnavailable in
        //      WorkflowEngineEnabledEndpointFilter.InvokeAsync; every row, the start included, then answers 500, and
        //      the problem's status below reads 500 too.
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        // Red: context.HttpContext.Response.Headers.RetryAfter = "30" set ahead of the 503 in
        //      WorkflowEngineEnabledEndpointFilter.InvokeAsync.
        response.Headers.RetryAfter.Should().BeNull();
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        // Red: the 500 mutation above; the problem's status is the response's.
        problem!.Status.Should().Be(503);
        // Red: ApiProblem.Of(..., detail: "x") in place of DisabledManufactureRunStarter.DisabledMessage in
        //      WorkflowEngineEnabledEndpointFilter.InvokeAsync.
        problem.Detail.Should().Be(DisabledManufactureRunStarter.DisabledMessage);
    }

    /// <summary>
    ///     Authorization runs first: an anonymous caller gets 401 and a caller without the role gets 403, never a 503
    ///     that reveals the host's configuration.
    /// </summary>
    [Fact]
    public async Task An_unauthenticated_or_unauthorized_caller_never_sees_the_503()
    {
        await using var factory = new ApiWebApplicationFactory(fixture.ConnectionString, Substitute.For<IAgentRuntime>());
        using var anonymous = factory.CreateClient();
        using var reader = Client(factory, "a-reader", "reader");

        var unauthenticated = await anonymous.PostAsJsonAsync("/api/workflow-runs", new { workIntent = "x", repository = "sandbox" });
        var forbidden = await reader.PostAsJsonAsync("/api/workflow-runs", new { workIntent = "x", repository = "sandbox" });

        // Red: .AllowAnonymous() added to the group in MapWorkflowRunsEndpoints; both callers then pass authorization,
        //      reach WorkflowEngineEnabledEndpointFilter and get its 503.
        unauthenticated.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    ///     A disabled host has no live channel: no run observer, no broadcaster among the hosted services, and no hub, so
    ///     a developer's request to the hub path is a 404.
    ///     Red: hoist the IWorkflowRunObserver registration out of the engine check: the observer list is not empty.
    ///     Red: hoist the broadcaster's hosted-service registration out: the hosted services include it.
    ///     Red: hoist MapHub out: the hub path answers something other than 404.
    /// </summary>
    [Fact]
    public async Task A_disabled_host_has_no_run_observer_broadcaster_or_hub()
    {
        await using var factory = new ApiWebApplicationFactory(fixture.ConnectionString, Substitute.For<IAgentRuntime>());
        using var client = Client(factory, "a-developer", "developer");

        factory.Services.GetServices<IWorkflowRunObserver>().Should().BeEmpty();
        factory.Services.GetServices<IHostedService>().Should().NotContain(s => s is WorkflowRunChangeBroadcaster);
        (await client.GetAsync(WorkflowRunsHub.Path)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static HttpClient Client(ApiWebApplicationFactory factory, string user, string role)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(HeaderTestAuthHandler.UserHeader, user);
        client.DefaultRequestHeaders.Add(HeaderTestAuthHandler.RolesHeader, role);
        return client;
    }
}
