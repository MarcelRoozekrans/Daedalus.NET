using Daedalus.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Thalos;

namespace Daedalus.Tests.Integration.Authentication;

/// <summary>
///     Every endpoint under <c>api/</c> on the real host requires authorization. The check reads the endpoint table, so
///     it holds for an MVC action, whose <c>[Authorize]</c> arrives as <see cref="IAuthorizeData"/> metadata, and for a
///     minimal API endpoint, whose <c>RequireAuthorization</c> adds the same metadata. It names no controller type and
///     survives the removal of MVC.
/// </summary>
/// <remarks>
///     The host is <see cref="ScratchWorkflowHost"/>, the one <c>EndpointContractSnapshotTests</c> uses, so the
///     workflow endpoints a production host maps are in the table too.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class ApiAuthorizationTests(PostgresFixture fixture)
{
    /// <summary>
    ///     Routes, relative to the root without a leading slash, that may be reached without a token. Explicit, and
    ///     empty today; an entry needs a ruling.
    /// </summary>
    private static readonly HashSet<string> AnonymousEndpoints = new(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public async Task Every_api_endpoint_requires_authorization()
    {
        await using var host = await ScratchWorkflowHost.StartAsync(fixture, Substitute.For<IAgentRuntime>());

        var unprotected = ApiEndpoints(host.Factory.Services)
            .Where(endpoint => !AnonymousEndpoints.Contains(Route(endpoint))
                && (endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Count == 0
                    || endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null))
            .Select(endpoint => endpoint.RoutePattern.RawText + " " + endpoint.DisplayName);

        // Red: dropping .RequireAuthorization from the group in AgentEndpoints.MapAgentEndpoints failed with
        //      found at least one item "api/agents/ HTTP: GET api/agents/ => GetAgents".
        // Red: dropping .RequireAuthorization("AgentUse") from the group in AgentMemoriesEndpoints.MapAgentMemoriesEndpoints
        //      failed with found at least one item
        //      "api/agent-memories/ HTTP: GET api/agent-memories/ => List".
        // Red: dropping .RequireAuthorization("AgentUse") from the group in AgentSessionsEndpoints.MapAgentSessionsEndpoints
        //      failed with found at least one item
        //      "api/agents/{agentId}/sessions HTTP: POST api/agents/{agentId}/sessions => CreateSession".
        // Red: dropping .RequireAuthorization("WorkflowResume") from the group in
        //      WorkflowRunsEndpoints.MapWorkflowRunsEndpoints failed with found at least one item
        //      "api/workflow-runs/ HTTP: POST api/workflow-runs/ => Start".
        unprotected.Should().BeEmpty("every route under api/ requires authorization unless AnonymousEndpoints lists it");
    }

    /// <summary>
    ///     The scan must see the whole table: a host that mapped nothing, or only part of it, under <c>api/</c> would
    ///     pass the check above. A ported route and a workflow route, which only a workflow-enabled host maps, must
    ///     both be there.
    /// </summary>
    [Fact]
    public async Task The_endpoint_table_holds_api_endpoints()
    {
        await using var host = await ScratchWorkflowHost.StartAsync(fixture, Substitute.For<IAgentRuntime>());

        var routes = ApiEndpoints(host.Factory.Services).Select(Route).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Red: changing the prefix filter in ApiEndpoints to apix/ failed with "Expected routes not to be empty".
        // Red: narrowing that filter to api/agents, a partial table, failed on the missing api/workflow-runs.
        routes.Should().NotBeEmpty();
        routes.Should().Contain("api/agents").And.Contain("api/workflow-runs");
    }

    /// <summary>Every route endpoint under <c>api/</c>, whichever stack mapped it.</summary>
    private static IEnumerable<RouteEndpoint> ApiEndpoints(IServiceProvider services) =>
        services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => Route(endpoint).StartsWith("api/", StringComparison.OrdinalIgnoreCase));

    private static string Route(RouteEndpoint endpoint) => endpoint.RoutePattern.RawText?.Trim('/') ?? string.Empty;
}
