using Daedalus.Api.Endpoints;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Http.Metadata;
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

    /// <summary>
    ///     Without <c>.WithTags</c> the OpenAPI document tags a minimal endpoint with the application name instead of
    ///     the controller name MVC used, and no snapshot sees it. The expectation is a tag on every endpoint, not a
    ///     list of controller types, so it survives the removal of MVC and a later port cannot pass by forgetting the
    ///     tag.
    /// </summary>
    [Fact]
    public async Task Every_minimal_api_endpoint_carries_an_explicit_tag()
    {
        await using var factory = new ApiWebApplicationFactory(fixture.ConnectionString, Substitute.For<IAgentRuntime>());

        var minimal = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<ControllerActionDescriptor>() is null
                && endpoint.RoutePattern.RawText?.TrimStart('/').StartsWith("api/", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        var untagged = minimal
            .Where(endpoint => endpoint.Metadata.GetMetadata<ITagsMetadata>() is not { Tags.Count: > 0 })
            .Select(endpoint => endpoint.RoutePattern.RawText);

        // Red: drop .WithTags("Agents") from AgentEndpoints.MapAgentEndpoints.
        untagged.Should().BeEmpty();
        minimal.Single(endpoint => endpoint.RoutePattern.RawText!.TrimStart('/').StartsWith("api/agents", StringComparison.Ordinal))
            .Metadata.GetMetadata<ITagsMetadata>()!.Tags.Should().Equal("Agents");
    }
}
