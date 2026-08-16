using System.Security.Claims;
using ZeroAlloc.Authorization;

namespace Daedalus.Agents.Security;

/// <summary>
///     <see cref="ISecurityContext"/> over a JWT <see cref="ClaimsPrincipal"/>: <see cref="Id"/> is the Keycloak subject
///     (<c>sub</c>, or <see cref="ClaimTypes.NameIdentifier"/> after inbound claim mapping, or the identity name),
///     <see cref="Roles"/> collects <see cref="ClaimTypes.Role"/> / <c>role</c> / <c>roles</c> claims (Keycloak realm roles
///     are mapped to <c>roles</c> by the realm's client scope; ASP.NET's JWT handler maps them to <see cref="ClaimTypes.Role"/>).
/// </summary>
/// <remarks>Unauthenticated principals map to <see cref="AnonymousSecurityContext.AnonymousId"/> with no roles.</remarks>
public sealed class ClaimsSecurityContext : ISecurityContext
{
    /// <summary>Creates a security context from <paramref name="principal"/>.</summary>
    public ClaimsSecurityContext(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        Id = FirstNonEmpty(principal, "sub")
             ?? FirstNonEmpty(principal, ClaimTypes.NameIdentifier)
             ?? NonEmpty(principal.Identity?.Name)
             ?? AnonymousSecurityContext.AnonymousId;

        Roles = principal.FindAll(IsRoleClaim)
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToHashSet(StringComparer.Ordinal);

        Claims = principal.Claims
            .GroupBy(c => c.Type, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public IReadOnlySet<string> Roles { get; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> Claims { get; }

    private static bool IsRoleClaim(Claim claim) =>
        claim.Type is ClaimTypes.Role or "role" or "roles";

    private static string? FirstNonEmpty(ClaimsPrincipal principal, string claimType) =>
        NonEmpty(principal.FindFirst(claimType)?.Value);

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
