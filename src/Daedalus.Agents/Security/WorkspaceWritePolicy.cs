using ZeroAlloc.Authorization;
using ZeroAlloc.Results;

namespace Daedalus.Agents.Security;

/// <summary>
///     Tool policy <c>workspace-write</c>: the write boundary for a run's worktree, bound in <c>Thalos:ToolPolicies</c>
///     to <c>workspace__write_*</c>, <c>workspace__edit_*</c> and <c>roslyn__apply_*</c>. Passes for the
///     <see cref="WorkspaceWriterRole"/> role, and for <c>developer</c> or <c>admin</c>.
/// </summary>
/// <remarks>
///     <para>
///     <b>Only a workflow caller ever holds <see cref="WorkspaceWriterRole"/>.</b>
///     <see cref="Daedalus.Agents.Workflow.WorkflowCaller"/> adds it when
///     <see cref="Daedalus.Agents.Workflow.WorkspaceWriteGrant.GrantFor"/> returns an entry: the run's current node is
///     a configured <c>Thalos:Workflow:WriteGrants</c> pair, pinned in the run's manifest, and the run's write-once
///     starter held <c>developer</c> or <c>admin</c>. A scheduled run's <c>["reader"]</c> and an ungranted workflow
///     caller's <c>["workflow"]</c> both fail.
///     </para>
///     <para>
///     <b>Passing this policy is not enough to write a workspace.</b> <c>developer</c> and <c>admin</c> pass so a
///     human's own chat turn keeps <c>roslyn__apply_*</c> against the host's loaded solution, which the
///     <c>developer</c> binding this replaces already allowed. The <c>workspace__*</c> tools act only on the workspace
///     the caller's <c>thalos.run_id</c> claim names, and a chat caller carries none:
///     <see cref="ClaimsSecurityContext"/> drops every inbound <c>thalos.*</c> claim, so a token cannot supply one.
///     </para>
/// </remarks>
[Policy(PolicyName)]
public sealed class WorkspaceWritePolicy : IAuthorizationPolicy
{
    /// <summary>The <c>[Policy]</c> name to reference from tool-policy bindings.</summary>
    public const string PolicyName = "workspace-write";

    /// <summary>The role a granted workflow caller holds. Never configured for a human or a schedule.</summary>
    public const string WorkspaceWriterRole = "workspace-writer";

    /// <inheritdoc />
    public ValueTask<UnitResult<AuthorizationFailure>> EvaluateAsync(ISecurityContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return new(ctx.Roles.Contains(DeveloperPolicy.DeveloperRole)
                   || ctx.Roles.Contains(DeveloperPolicy.AdminRole)
                   || ctx.Roles.Contains(WorkspaceWriterRole)
            ? UnitResult<AuthorizationFailure>.Success()
            : UnitResult<AuthorizationFailure>.Failure(new AuthorizationFailure("role", "workspace-writer, developer or admin role required")));
    }
}
