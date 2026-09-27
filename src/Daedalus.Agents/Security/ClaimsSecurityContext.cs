using System.Security.Claims;
using ZeroAlloc.Authorization;

namespace Daedalus.Agents.Security;

/// <summary>
///     <see cref="ISecurityContext"/> over a JWT <see cref="ClaimsPrincipal"/>: <see cref="Id"/> is the Keycloak subject
///     (<c>sub</c>, or <see cref="ClaimTypes.NameIdentifier"/> after inbound claim mapping, or the identity name),
///     <see cref="Roles"/> collects <see cref="ClaimTypes.Role"/> / <c>role</c> / <c>roles</c> claims (Keycloak realm roles
///     are mapped to <c>roles</c> by the realm's client scope; ASP.NET's JWT handler maps them to <see cref="ClaimTypes.Role"/>).
/// </summary>
/// <remarks>
///     <para>Unauthenticated principals map to <see cref="AnonymousSecurityContext.AnonymousId"/> with no roles.</para>
///     <para>
///     <b>No inbound <c>thalos.*</c> claim survives.</b> <see cref="Thalos.Workspaces.RunWorkspaceClaims.RunId"/> decides
///     which run's worktree the <c>workspace__*</c> tools act on and which run's own MCP servers a run-scoped call is
///     routed to, and <see cref="Thalos.Workspaces.RunWorkspaceClaims.WriteExtensions"/> decides what may be written
///     there. Those claims are set only by <see cref="Daedalus.Agents.Workflow.WorkflowCaller"/>, from the run row and
///     reviewed config. A token that carried <c>thalos.run_id</c> would otherwise steer a chat user's
///     <c>workspace__*</c> and <c>roslyn__*</c> calls into a run's worktree and server, held back only by the identity
///     provider's mapper configuration. The prefix is matched ignoring case, so no spelling of it gets through.
///     </para>
///     <para>
///     <b>Nor does an inbound <see cref="WorkspaceWritePolicy.WorkspaceWriterRole"/> role.</b> Only a granted
///     <see cref="Daedalus.Agents.Workflow.WorkflowCaller"/> holds it; a realm role of that name is dropped here, so the
///     role never names a human, whatever the identity provider is configured to issue.
///     </para>
/// </remarks>
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
            .Where(v => !string.IsNullOrWhiteSpace(v)
                        && !string.Equals(v, WorkspaceWritePolicy.WorkspaceWriterRole, StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.Ordinal);

        Claims = principal.Claims
            .Where(c => !IsHostOnlyClaim(c))
            .GroupBy(c => c.Type, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public IReadOnlySet<string> Roles { get; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> Claims { get; }

    /// <summary>The prefix of every claim only host code may set on a caller (see this type's remarks).</summary>
    internal const string HostOnlyClaimPrefix = "thalos.";

    private static bool IsHostOnlyClaim(Claim claim) =>
        claim.Type.StartsWith(HostOnlyClaimPrefix, StringComparison.OrdinalIgnoreCase);

    private static bool IsRoleClaim(Claim claim) =>
        claim.Type is ClaimTypes.Role or "role" or "roles";

    private static string? FirstNonEmpty(ClaimsPrincipal principal, string claimType) =>
        NonEmpty(principal.FindFirst(claimType)?.Value);

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
