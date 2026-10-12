using Daedalus.Api.Hubs;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;

namespace Daedalus.Tests.Unit.Hubs;

/// <summary>The API reads a bearer token from the query string for the hubs and nowhere else.</summary>
public sealed class HubAccessTokenTests
{
    /// <summary>
    ///     Red: drop the path check: the API route reads the query token too.
    ///     Red: read a parameter named token instead of access_token.
    /// </summary>
    [Theory]
    [InlineData("/hubs/workflow-runs", "zqx-t")]
    [InlineData("/api/workflow-runs", null)]
    public async Task Only_a_hub_path_reads_the_query_token(string path, string? expected)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = path;
        http.Request.QueryString = new QueryString("?access_token=zqx-t");
        var context = new MessageReceivedContext(
            http,
            new AuthenticationScheme(JwtBearerDefaults.AuthenticationScheme, null, typeof(JwtBearerHandler)),
            new JwtBearerOptions());

        await HubAccessToken.ReadFromQueryAsync(context);

        context.Token.Should().Be(expected);
    }
}
