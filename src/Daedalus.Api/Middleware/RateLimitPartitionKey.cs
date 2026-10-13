using Daedalus.Agents.Security;

namespace Daedalus.Api.Middleware;

/// <summary>
///     Who a request counts against under the global rate limit: the signed-in caller's subject, resolved as
///     <see cref="ClaimsSecurityContext.SubjectOf"/> resolves every caller id, or, for a request with no signed-in
///     caller, its remote address. A Keycloak token carries <c>sub</c> and no <c>ClaimTypes.Name</c>, so a key on
///     the identity name alone would fall back to the address for every real user, and behind an ingress every user
///     would share one budget.
/// </summary>
public static class RateLimitPartitionKey
{
    /// <summary>The partition key of <paramref name="context"/>'s request.</summary>
    public static string For(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var user = context.User;
        if (user.Identity is { IsAuthenticated: true } && ClaimsSecurityContext.SubjectOf(user) is { } subject)
        {
            return "user:" + subject;
        }

        return "address:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    }
}
