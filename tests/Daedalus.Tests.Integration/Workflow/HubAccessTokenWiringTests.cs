using Daedalus.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Thalos;

namespace Daedalus.Tests.Integration.Workflow;

/// <summary>The real Api's JWT bearer scheme reads the hub's query token, and only on a hub path.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class HubAccessTokenWiringTests(PostgresFixture fixture)
{
    /// <summary>
    ///     The scheme's MessageReceived event takes access_token from a hub request and leaves an API request alone.
    ///     Red: delete the JwtBearerEvents wiring line in Program.cs: the hub request's token stays null.
    /// </summary>
    [Theory]
    [InlineData("/hubs/workflow-runs", "x")]
    [InlineData("/api/workflow-runs", null)]
    public async Task The_bearer_scheme_reads_the_query_token_on_a_hub_path_only(string path, string? expected)
    {
        await using var factory = new ApiWebApplicationFactory(fixture.ConnectionString, Substitute.For<IAgentRuntime>());
        var options = factory.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);
        var http = new DefaultHttpContext { RequestServices = factory.Services };
        http.Request.Path = path;
        http.Request.QueryString = new QueryString("?access_token=x");
        var context = new MessageReceivedContext(
            http,
            new AuthenticationScheme(JwtBearerDefaults.AuthenticationScheme, null, typeof(JwtBearerHandler)),
            options);

        await options.Events.MessageReceived(context);

        context.Token.Should().Be(expected);
    }
}
