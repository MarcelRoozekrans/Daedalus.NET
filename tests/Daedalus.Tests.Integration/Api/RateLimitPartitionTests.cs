using Daedalus.Tests.Integration.Fixtures;
using Thalos;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Api;

/// <summary>
///     The Api's global rate limit, 100 requests a minute, is each signed-in user's own: it partitions by the subject
///     authentication set, so one user spending the budget leaves another's untouched.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class RateLimitPartitionTests(PostgresFixture fixture)
{
    /// <summary>The global limiter's permit count per window, as <c>Program.cs</c> configures it.</summary>
    private const int GlobalPermits = 100;

    /// <summary>
    ///     A user who spends the whole budget is refused with 429, while a second user on the same address is not. Both
    ///     carry a Keycloak token's claim shape, with no identity name.
    ///     Red: run <c>UseRateLimiter</c> before <c>UseAuthentication</c>: both users fall into one partition by
    ///     address, and the second user's request is a 429.
    ///     Red: key the partition on the identity name: it is null, both users fall back to the address, and the
    ///     second user's request is a 429.
    ///     Red: drop <c>UseRateLimiter</c>: the first user's extra request is not a 429.
    /// </summary>
    [Fact]
    public async Task Each_signed_in_user_has_a_budget_of_their_own()
    {
        await using var factory = new ApiWebApplicationFactory(fixture.ConnectionString, Substitute.For<IAgentRuntime>());
        using var first = Client(factory, "first-user");
        using var second = Client(factory, "second-user");

        for (var i = 0; i < GlobalPermits; i++)
        {
            using var spent = await first.GetAsync("/api/tasks?pageSize=1");
            spent.StatusCode.Should().Be(HttpStatusCode.OK, "request {0} is within the first user's budget", i + 1);
        }

        using var refused = await first.GetAsync("/api/tasks?pageSize=1");
        using var other = await second.GetAsync("/api/tasks?pageSize=1");

        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "the first user has spent the budget");
        other.StatusCode.Should().Be(HttpStatusCode.OK, "the second user's budget is their own");
    }

    private static HttpClient Client(ApiWebApplicationFactory factory, string user)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(HeaderTestAuthHandler.UserHeader, user);
        client.DefaultRequestHeaders.Add(HeaderTestAuthHandler.RolesHeader, "reader");
        // A real token's claims: sub but no ClaimTypes.Name, so the user's identity name is null.
        client.DefaultRequestHeaders.Add(HeaderTestAuthHandler.KeycloakShapeHeader, "true");
        return client;
    }
}
