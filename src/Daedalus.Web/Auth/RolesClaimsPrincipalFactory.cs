using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication.Internal;

namespace Daedalus.Web.Auth;

/// <summary>
///     Keycloak sends realm roles as one JSON array claim named <c>roles</c>; Blazor would keep it as one string, so
///     <c>IsInRole</c> never matched. This splits it into one role claim per role.
/// </summary>
public sealed class RolesClaimsPrincipalFactory(IAccessTokenProviderAccessor accessor)
    : AccountClaimsPrincipalFactory<RemoteUserAccount>(accessor)
{
    private const string RolesClaim = "roles";

    /// <inheritdoc />
    public override async ValueTask<ClaimsPrincipal> CreateUserAsync(
        RemoteUserAccount account, RemoteAuthenticationUserOptions options)
    {
        var user = await base.CreateUserAsync(account, options);
        if (user.Identity is not ClaimsIdentity { IsAuthenticated: true } identity
            || !account.AdditionalProperties.TryGetValue(RolesClaim, out var raw)
            || raw is not JsonElement { ValueKind: JsonValueKind.Array } roles)
        {
            return user;
        }

        foreach (var claim in identity.FindAll(RolesClaim).ToList())
        {
            identity.RemoveClaim(claim);
        }

        foreach (var role in roles.EnumerateArray())
        {
            if (role.ValueKind == JsonValueKind.String && role.GetString() is { Length: > 0 } name)
            {
                identity.AddClaim(new Claim(options.RoleClaim ?? ClaimTypes.Role, name));
            }
        }

        return user;
    }
}
