using System.Security.Claims;
using System.Text.Json;
using Daedalus.Web.Auth;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication.Internal;

namespace Daedalus.Tests.Unit.Web;

/// <summary>Keycloak's roles array becomes one role claim each, so IsInRole works in the Web.</summary>
public sealed class RolesClaimsPrincipalFactoryTests
{
    /// <summary>
    ///     A roles array of developer and admin gives two role claims and no raw roles claim.
    ///     Red: add the array as one claim. Red: stop after the first role. Red: keep the raw claim as well.
    /// </summary>
    [Fact]
    public async Task Each_role_becomes_a_claim()
    {
        // AdditionalProperties is the JSON extension data a deserialized account carries; a new one has none.
        var account = new RemoteUserAccount
        {
            AdditionalProperties = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["roles"] = JsonDocument.Parse("""["developer","admin"]""").RootElement,
                ["name"] = JsonDocument.Parse("\"dev\"").RootElement,
            },
        };
        var factory = new RolesClaimsPrincipalFactory(Substitute.For<IAccessTokenProviderAccessor>());

        var user = await factory.CreateUserAsync(
            account,
            new RemoteAuthenticationUserOptions { RoleClaim = ClaimTypes.Role, NameClaim = "name", AuthenticationType = "oidc" });

        user.IsInRole("developer").Should().BeTrue();
        user.IsInRole("admin").Should().BeTrue();
        user.FindAll("roles").Should().BeEmpty();
    }
}
