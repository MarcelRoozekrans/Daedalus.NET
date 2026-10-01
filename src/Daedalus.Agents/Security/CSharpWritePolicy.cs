using Thalos.Workspaces;
using ZeroAlloc.Authorization;
using ZeroAlloc.Results;

namespace Daedalus.Agents.Security;

/// <summary>
///     Tool policy <c>csharp-write</c>, bound in <c>Thalos:ToolPolicies</c> to <c>roslyn__apply_*</c> on a host whose
///     <c>roslyn</c> server is run-scoped. Passes for <c>developer</c> or <c>admin</c>, and for a
///     <see cref="WorkspaceWritePolicy.WorkspaceWriterRole"/> caller only when its
///     <see cref="RunWorkspaceClaims.WriteExtensions"/> claim includes <c>.cs</c>.
/// </summary>
/// <remarks>
///     <para>
///     <b>Why not <c>workspace-write</c>.</b> Ruling R29's extension allow-list is enforced by the <c>workspace__*</c>
///     tools, not by RoslynCodeLens: a code action writes whatever <c>.cs</c> documents it changes, with no extension
///     check. So a node whose grant does not include <c>.cs</c>, such as a future <c>.md</c>-only grant, must not reach
///     <c>roslyn__apply_*</c> at all, even though it holds <see cref="WorkspaceWritePolicy.WorkspaceWriterRole"/>.
///     </para>
///     <para>
///     <b>Where a call lands.</b> A workflow caller carries <see cref="RunWorkspaceClaims.RunId"/>, so the run-scoped
///     <c>roslyn</c> source serves it only from its own run's server, over its own worktree. <c>AddDaedalusAgents</c>
///     refuses to start a workflow-enabled host that binds this policy to an MCP source that is not run-scoped, so a
///     granted run can never apply a code action to the host's own solution. With the engine off there is no workflow
///     caller, and this policy admits exactly what <c>developer</c> does. A <c>developer</c> or <c>admin</c> chat turn
///     carries no run claim, because <see cref="ClaimsSecurityContext"/> drops every inbound <c>thalos.*</c> claim, so
///     it is served by the host server, as it was when this pattern was bound to <c>developer</c>.
///     </para>
///     <para>
///     Every pattern bound to this policy is audited like <c>workspace-write</c>: see
///     <see cref="AuditingToolAuthorizer"/>.
///     </para>
/// </remarks>
[Policy(PolicyName)]
public sealed class CSharpWritePolicy : IAuthorizationPolicy
{
    /// <summary>The <c>[Policy]</c> name to reference from tool-policy bindings.</summary>
    public const string PolicyName = "csharp-write";

    /// <summary>The extension a workflow caller's grant must include.</summary>
    public const string CSharpExtension = ".cs";

    /// <inheritdoc />
    public ValueTask<UnitResult<AuthorizationFailure>> EvaluateAsync(ISecurityContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (ctx.Roles.Contains(DeveloperPolicy.DeveloperRole) || ctx.Roles.Contains(DeveloperPolicy.AdminRole))
        {
            return new(UnitResult<AuthorizationFailure>.Success());
        }

        if (!ctx.Roles.Contains(WorkspaceWritePolicy.WorkspaceWriterRole))
        {
            return new(UnitResult<AuthorizationFailure>.Failure(
                new AuthorizationFailure("role", "workspace-writer, developer or admin role required")));
        }

        return new(GrantsCSharp(ctx)
            ? UnitResult<AuthorizationFailure>.Success()
            : UnitResult<AuthorizationFailure>.Failure(
                new AuthorizationFailure("extension", "this node's write grant does not include .cs")));
    }

    /// <summary>
    ///     Whether the caller's write-extension claim lists <c>.cs</c>, parsed by the same Thalos reader the
    ///     <c>workspace__*</c> tools use. An absent claim grants nothing here, unlike there: a workspace writer with no
    ///     extension list is a caller built outside <c>WorkflowCaller</c>, and fails closed.
    /// </summary>
    private static bool GrantsCSharp(ISecurityContext ctx) =>
        RunWorkspaceClaims.WriteExtensionsOf(ctx) is { } extensions && extensions.Contains(CSharpExtension);
}
