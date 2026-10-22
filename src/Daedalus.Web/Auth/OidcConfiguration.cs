using System.Security.Claims;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.Extensions.Configuration;

namespace Daedalus.Web.Auth;

/// <summary>
///     The console's OIDC settings: provider-agnostic, read from the <c>Oidc</c> section. Keycloak sends realm roles as one
///     JSON array claim; <see cref="RolesClaimsPrincipalFactory"/> splits it into role claims so <c>IsInRole</c> works.
/// </summary>
internal static class OidcConfiguration
{
    /// <summary>The configuration section the provider options bind from.</summary>
    internal const string SectionName = "Oidc";

    /// <summary>Binds the <c>Oidc</c> section onto <paramref name="options"/>' provider options and names the role claim.</summary>
    internal static void Apply(IConfiguration configuration, RemoteAuthenticationOptions<OidcProviderOptions> options)
    {
        configuration.Bind(SectionName, options.ProviderOptions);
        options.UserOptions.RoleClaim = ClaimTypes.Role;
    }
}
