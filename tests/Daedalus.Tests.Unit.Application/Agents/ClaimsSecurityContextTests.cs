using System.Security.Claims;
using Daedalus.Agents.Security;
using ZeroAlloc.Authorization;

namespace Daedalus.Tests.Unit.Application.Agents;

public sealed class ClaimsSecurityContextTests
{
    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "Bearer", nameType: ClaimTypes.Name, roleType: ClaimTypes.Role));

    [Fact]
    public void Id_prefers_sub_over_name_identifier_and_name()
    {
        var ctx = new ClaimsSecurityContext(Principal(
            new Claim(ClaimTypes.Name, "alice"),
            new Claim(ClaimTypes.NameIdentifier, "nameid-1"),
            new Claim("sub", "sub-1")));

        ctx.Id.Should().Be("sub-1");
    }

    [Fact]
    public void Id_falls_back_to_name_identifier_when_sub_was_mapped_inbound()
    {
        var ctx = new ClaimsSecurityContext(Principal(new Claim(ClaimTypes.NameIdentifier, "nameid-1"), new Claim(ClaimTypes.Name, "alice")));

        ctx.Id.Should().Be("nameid-1");
    }

    [Fact]
    public void Id_falls_back_to_identity_name_then_anonymous()
    {
        new ClaimsSecurityContext(Principal(new Claim(ClaimTypes.Name, "alice"))).Id.Should().Be("alice");
        new ClaimsSecurityContext(new ClaimsPrincipal(new ClaimsIdentity())).Id.Should().Be(AnonymousSecurityContext.AnonymousId);
    }

    [Fact]
    public void Roles_collect_role_claim_types_from_aspnet_and_keycloak()
    {
        var ctx = new ClaimsSecurityContext(Principal(
            new Claim("sub", "u"),
            new Claim(ClaimTypes.Role, "admin"),
            new Claim("role", "developer"),
            new Claim("roles", "viewer"),
            new Claim("scope", "openid")));

        ctx.Roles.Should().BeEquivalentTo(["admin", "developer", "viewer"]);
        ctx.Roles.Contains("openid").Should().BeFalse();
    }

    [Fact]
    public void Claims_keep_the_first_value_per_type()
    {
        var ctx = new ClaimsSecurityContext(Principal(new Claim("sub", "u"), new Claim("roles", "a"), new Claim("roles", "b")));

        ctx.Claims["sub"].Should().Be("u");
        ctx.Claims["roles"].Should().Be("a");
    }

    [Fact]
    public async Task Developer_policy_passes_for_developer_or_admin_only()
    {
        var policy = new DeveloperPolicy();

        (await policy.EvaluateAsync(new ClaimsSecurityContext(Principal(new Claim("roles", "developer"))))).IsSuccess.Should().BeTrue();
        (await policy.EvaluateAsync(new ClaimsSecurityContext(Principal(new Claim(ClaimTypes.Role, "admin"))))).IsSuccess.Should().BeTrue();

        var denied = await policy.EvaluateAsync(new ClaimsSecurityContext(Principal(new Claim("roles", "viewer"))));
        denied.IsFailure.Should().BeTrue();
        denied.Error.Code.Should().Be("role");
    }
}
