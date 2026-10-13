using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Fixtures;

/// <summary>
///     Test authentication scheme for in-process API hosts: the caller is whoever <c>X-Test-User</c> names (becomes the
///     <c>sub</c> claim); <c>X-Test-Roles</c> is a comma-separated role list; <c>X-Test-Keycloak-Shape</c> drops the
///     <see cref="ClaimTypes.Name"/> claim for a real token's shape. No header → no result → 401 on protected routes.
/// </summary>
internal sealed class HeaderTestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "TestHeader";
    public const string UserHeader = "X-Test-User";
    public const string RolesHeader = "X-Test-Roles";

    /// <summary>
    ///     Present: the caller carries a Keycloak token's claim shape, <c>sub</c>, <c>preferred_username</c> and
    ///     <c>name</c>, and no <see cref="ClaimTypes.Name"/>, so its identity name is null as a real token's is.
    /// </summary>
    public const string KeycloakShapeHeader = "X-Test-Keycloak-Shape";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers[UserHeader].ToString();
        if (string.IsNullOrWhiteSpace(user))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = Request.Headers.ContainsKey(KeycloakShapeHeader)
            ? new List<Claim> { new("sub", user), new("preferred_username", user), new("name", user) }
            : new List<Claim> { new("sub", user), new(ClaimTypes.Name, user) };
        claims.AddRange(Request.Headers[RolesHeader].ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(role => new Claim(ClaimTypes.Role, role)));

        var ticket = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
