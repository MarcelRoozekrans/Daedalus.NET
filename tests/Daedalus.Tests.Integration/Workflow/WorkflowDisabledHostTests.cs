using Daedalus.Agents.Workflow;
using Daedalus.Api.Controllers;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Mvc;
using Thalos;

namespace Daedalus.Tests.Integration.Workflow;

/// <summary>
///     <c>WorkflowRunsController</c> on a host with <c>Thalos:Workflow:Enabled=false</c>, which is
///     <see cref="ApiWebApplicationFactory"/>'s default. Such a host registers no <c>WorkflowRunGateway</c>, so before
///     the <see cref="WorkflowEngineEnabledAttribute"/> filter every action failed controller activation with a bare 500.
///     Red, for every test that expects 503: remove <c>[WorkflowEngineEnabled]</c> from the controller, and the
///     answer is a 500.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class WorkflowDisabledHostTests(PostgresFixture fixture)
{
    /// <summary>
    ///     A write that starts a run, a read, and two writes on a run all answer the same 503 problem details naming
    ///     the disabled engine, with no <c>Retry-After</c>, because no retry gets past a host setting. Red, per
    ///     assertion: remove the attribute, and the status fails; add a <c>Retry-After</c> to the filter's answer, and
    ///     the header fails; change the filter's detail, and the detail fails.
    /// </summary>
    [Theory]
    [InlineData("POST", "/api/workflow-runs")]
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

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter.Should().BeNull();
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Status.Should().Be(503);
        problem.Detail.Should().Be(DisabledManufactureRunStarter.DisabledMessage);
    }

    /// <summary>
    ///     Authorization runs first: an anonymous caller gets 401 and a caller without the role gets 403, never a 503
    ///     that reveals the host's configuration. Red, per assertion: move the check ahead of authorization, for example
    ///     by answering from middleware, and the first or second status fails.
    /// </summary>
    [Fact]
    public async Task An_unauthenticated_or_unauthorized_caller_never_sees_the_503()
    {
        await using var factory = new ApiWebApplicationFactory(fixture.ConnectionString, Substitute.For<IAgentRuntime>());
        using var anonymous = factory.CreateClient();
        using var reader = Client(factory, "a-reader", "reader");

        var unauthenticated = await anonymous.PostAsJsonAsync("/api/workflow-runs", new { workIntent = "x", repository = "sandbox" });
        var forbidden = await reader.PostAsJsonAsync("/api/workflow-runs", new { workIntent = "x", repository = "sandbox" });

        unauthenticated.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private static HttpClient Client(ApiWebApplicationFactory factory, string user, string role)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(HeaderTestAuthHandler.UserHeader, user);
        client.DefaultRequestHeaders.Add(HeaderTestAuthHandler.RolesHeader, role);
        return client;
    }
}
