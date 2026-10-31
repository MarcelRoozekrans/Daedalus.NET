using Daedalus.Api.Endpoints;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Thalos;

namespace Daedalus.Tests.Integration.Api;

/// <summary>
///     Every minimal API endpoint under <c>api/</c> on the real host is mapped through
///     <c>DaedalusEndpoints.MapApiGroup</c>, so it carries <see cref="DaedalusEndpointGroup"/> and with it the
///     ZeroAlloc validation filter. A port that maps its endpoints on a bare <c>MapGroup</c> serves binding failures as
///     a bare 400 and skips validation; this test names it.
/// </summary>
/// <remarks>
///     Before the first port, Task 6, the host mapped no minimal API endpoint under <c>api/</c> and the test checked an
///     empty set; since then it checks <c>AgentEndpoints</c> and every later group. Its Red line was applied with a
///     temporary <c>app.MapGet("/api/probe", ...)</c> in <c>MapDaedalusEndpoints</c>.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class EndpointGroupTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Every_minimal_api_endpoint_is_mapped_through_the_api_group()
    {
        await using var factory = new ApiWebApplicationFactory(fixture.ConnectionString, Substitute.For<IAgentRuntime>());

        var unguarded = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<ControllerActionDescriptor>() is null
                && endpoint.RoutePattern.RawText?.TrimStart('/').StartsWith("api/", StringComparison.OrdinalIgnoreCase) == true
                && endpoint.Metadata.GetMetadata<DaedalusEndpointGroup>() is null)
            .Select(endpoint => endpoint.RoutePattern.RawText);

        // Red: map an endpoint under api/ in MapDaedalusEndpoints with app.MapGet rather than through MapApiGroup.
        unguarded.Should().BeEmpty();
    }
}
