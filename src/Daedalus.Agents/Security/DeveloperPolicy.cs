using ZeroAlloc.Authorization;
using ZeroAlloc.Results;

namespace Daedalus.Agents.Security;

/// <summary>
///     Tool policy <c>developer</c>: passes when the caller has the <c>developer</c> or <c>admin</c> role. Bind it to
///     mutating tools via <c>Thalos:ToolPolicies</c> (for example <c>roslyn__apply_*</c> → <c>developer</c>).
/// </summary>
[Policy(PolicyName)]
public sealed class DeveloperPolicy : IAuthorizationPolicy
{
    /// <summary>The <c>[Policy]</c> name to reference from tool-policy bindings.</summary>
    public const string PolicyName = "developer";

    /// <summary>Role that satisfies the policy on its own.</summary>
    public const string DeveloperRole = "developer";

    /// <summary>Role that satisfies the policy on its own (matches Thalos's admin literal for cross-owner session access).</summary>
    public const string AdminRole = "admin";

    /// <inheritdoc />
    public ValueTask<UnitResult<AuthorizationFailure>> EvaluateAsync(ISecurityContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return new(ctx.Roles.Contains(DeveloperRole) || ctx.Roles.Contains(AdminRole)
            ? UnitResult<AuthorizationFailure>.Success()
            : UnitResult<AuthorizationFailure>.Failure(new AuthorizationFailure("role", "developer or admin role required")));
    }
}
