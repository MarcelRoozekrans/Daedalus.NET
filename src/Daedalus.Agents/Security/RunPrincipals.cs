using Thalos.Workflow;
using ZeroAlloc.Authorization;

namespace Daedalus.Agents.Security;

/// <summary>
///     The one place a caller becomes a <see cref="RunPrincipal"/>: the REST start and resume endpoints and the
///     <c>manufacture__start</c> tool all build the principal a run records through <see cref="From"/>, so the
///     starter and the approver of a run are always shaped the same way.
/// </summary>
public static class RunPrincipals
{
    /// <summary>
    ///     The caller's <see cref="ISecurityContext.Id"/>, and its roles sorted ordinally so the stored JSON is
    ///     stable across calls. <paramref name="displayName"/> is audit text only, for a reader of the run or of
    ///     the pull request it opens; it never takes part in authorization.
    /// </summary>
    public static RunPrincipal From(ISecurityContext caller, string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(caller);

        return new RunPrincipal(caller.Id, [.. caller.Roles.Order(StringComparer.Ordinal)]) { DisplayName = displayName };
    }
}
