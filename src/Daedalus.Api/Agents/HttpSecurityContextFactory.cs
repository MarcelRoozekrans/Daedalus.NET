using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Daedalus.Agents.Security;
using ZeroAlloc.Authorization;

namespace Daedalus.Api.Agents;

/// <summary>
///     Builds the Thalos <see cref="ISecurityContext"/> for the current HTTP caller. Defence in depth next to
///     <c>[Authorize]</c>: an unauthenticated principal, or one without a usable subject, is never turned into a caller —
///     Thalos would otherwise happily create sessions owned by <see cref="AnonymousSecurityContext.AnonymousId"/>.
/// </summary>
internal static class HttpSecurityContextFactory
{
    /// <summary>
    ///     Returns <see langword="true"/> with the caller when <paramref name="user"/> is authenticated and yields a real
    ///     subject id; <see langword="false"/> otherwise (respond 401).
    /// </summary>
    public static bool TryCreate(ClaimsPrincipal? user, [NotNullWhen(true)] out ISecurityContext? caller)
    {
        caller = null;
        if (user?.Identity is not { IsAuthenticated: true })
        {
            return false;
        }

        var context = new ClaimsSecurityContext(user);
        if (string.Equals(context.Id, AnonymousSecurityContext.AnonymousId, StringComparison.Ordinal))
        {
            return false;
        }

        caller = context;
        return true;
    }
}
