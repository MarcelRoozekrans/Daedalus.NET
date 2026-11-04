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

        var unprotected = host.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.TrimStart('/').StartsWith("api/", StringComparison.OrdinalIgnoreCase) == true
                && !AnonymousEndpoints.Contains(endpoint.RoutePattern.RawText.TrimStart('/'))
                && (endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Count == 0
                    || endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null))
            .Select(endpoint => endpoint.RoutePattern.RawText + " " + endpoint.DisplayName);

        // Red: dropping .RequireAuthorization from the group in AgentEndpoints.MapAgentEndpoints failed with
        //      found at least one item "api/agents/ HTTP: GET api/agents/ => GetAgents".
        // Red: dropping the class-level [Authorize] from AgentMemoriesController failed with found at least one item
        //      "api/agent-memories ...AgentMemoriesController.List". The same on ProjectsController stays green, as
        //      every action there carries its own policy, so it is the wrong controller for this Red.
        unprotected.Should().BeEmpty("every route under api/ requires authorization unless AnonymousEndpoints lists it");
    }

    /// <summary>
    ///     The scan must see the table: a host that mapped nothing under <c>api/</c> would pass the check above.
    /// </summary>
    [Fact]
    public async Task The_endpoint_table_holds_api_endpoints()
    {
        await using var host = await ScratchWorkflowHost.StartAsync(fixture, Substitute.For<IAgentRuntime>());

        var count = host.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Count(endpoint => endpoint.RoutePattern.RawText?.TrimStart('/').StartsWith("api/", StringComparison.OrdinalIgnoreCase) == true);

        // Red: map the endpoints under a prefix other than api/.
        count.Should().BeGreaterThan(0);
    }
}
