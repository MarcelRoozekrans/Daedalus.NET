using System.Net;
using System.Security.Claims;
using Daedalus.Api.Middleware;
using Microsoft.AspNetCore.Http;

namespace Daedalus.Tests.Unit.Security;

/// <summary>Who a request counts against under the global rate limit.</summary>
public sealed class RateLimitPartitionKeyTests
{
    private static readonly IPAddress Ingress = IPAddress.Parse("10.0.0.7");

    /// <summary>
    ///     A Keycloak token's principal, with sub, preferred_username, name and roles but no ClaimTypes.Name, is keyed on
    ///     its subject, so two such users behind one ingress address have two partitions.
    ///     Red: key on the identity name: it is null, and both users get the address key.
    /// </summary>
    [Fact]
    public void A_keycloak_shaped_user_is_keyed_on_the_subject()
    {
        var first = RateLimitPartitionKey.For(Request(Keycloak("sub-1", "alice")));
        var second = RateLimitPartitionKey.For(Request(Keycloak("sub-2", "bob")));

        first.Should().Be("user:sub-1");
        second.Should().Be("user:sub-2");
    }

    /// <summary>
    ///     A principal whose sub inbound claim mapping renamed to ClaimTypes.NameIdentifier is keyed on that.
    ///     Red: key on the identity name: it is null, and the key falls back to the address.
    /// </summary>
    [Fact]
    public void A_mapped_subject_is_keyed_on_the_name_identifier()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "sub-3"), new Claim("preferred_username", "carol")], "AuthenticationTypes.Federation"));

        RateLimitPartitionKey.For(Request(user)).Should().Be("user:sub-3");
    }

    /// <summary>
    ///     A request with no signed-in caller is keyed on its remote address, apart from any user key.
    ///     Red: key every anonymous request on one constant: the address is missing from the key.
    /// </summary>
    [Fact]
    public void An_anonymous_request_is_keyed_on_its_address()
    {
        RateLimitPartitionKey.For(Request(new ClaimsPrincipal(new ClaimsIdentity()))).Should().Be("address:10.0.0.7");
    }

    private static ClaimsPrincipal Keycloak(string subject, string username) =>
        new(new ClaimsIdentity(
            [
                new Claim("sub", subject),
                new Claim("preferred_username", username),
                new Claim("name", username),
                new Claim("roles", "developer"),
            ],
            "AuthenticationTypes.Federation"));

    private static DefaultHttpContext Request(ClaimsPrincipal user)
    {
        var context = new DefaultHttpContext { User = user };
        context.Connection.RemoteIpAddress = Ingress;
        return context;
    }
}
