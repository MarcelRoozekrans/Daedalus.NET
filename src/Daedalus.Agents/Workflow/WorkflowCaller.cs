using System.Collections.Frozen;
using System.Text;
using Daedalus.Agents.Security;
using Thalos.Memory;
using Thalos.Workflow;
using Thalos.Workspaces;
using ZeroAlloc.Authorization;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     The <see cref="ISecurityContext"/> a workflow run's agent turns execute as. A run has no human behind it,
///     so its identity is derived from the run itself rather than borrowed from whoever started it — the same
///     reasoning <see cref="Daedalus.Agents.Scheduling.DetachedPrincipal"/> gives for scheduled runs: an ambient
///     principal's access could change hours into a long-lived run with no live turn around to notice.
///     <para>
///     <b><see cref="Id"/> and <see cref="MemoryOwnerId"/> deliberately diverge.</b> <c>Id</c> is per-run — it is
///     what <c>Thalos.Tools.DefaultToolAuthorizer</c> evaluates and what makes one run's tool calls auditable
///     separately from another's, so it must keep changing every run. A memory owner that changed every run would
///     mean nothing a role writes is ever readable again: run B calls itself a different owner than run A, so
///     <c>(owner, null)</c> for A is permanently out of scope for B. <see cref="MemoryOwnerId"/> fixes that by
///     naming the <em>process</em> instead of the run, so every run of the same process reads what an earlier one
///     wrote. Changing <c>Id</c> itself to achieve this would be the wrong fix: it would collapse every run of a
///     process onto one authorization identity, and <c>WorkflowCallerTests</c> — via <c>MemoryOwnerId</c> and
///     <c>Id</c> disagreeing on whether they vary across runs — exists to catch exactly that.
///     </para>
/// </summary>
/// <remarks>
///     <see cref="WorkflowNodeDispatcher"/> only ever forwards this to <c>SubagentRunRequest.Caller</c> — it never
///     inspects it — so the single <c>"workflow"</c> role is exactly what <c>Thalos:ToolPolicies</c> needs to bind
///     a policy to every workflow-run tool call, the same way <c>schedule:daedalus</c>'s <c>["reader"]</c> role
///     binds <see cref="Daedalus.Agents.Security.DeveloperPolicy"/> for detached runs.
///     <para>
///     <b>This role denies by binding, and allows by default.</b> <c>Thalos.Tools.DefaultToolAuthorizer</c>
///     evaluates every <c>ToolPolicies</c> binding whose pattern matches the tool and allows the call when
///     <em>no</em> binding matches — so a <c>"workflow"</c> principal is denied exactly what is bound to
///     <c>developer</c>, and to <c>workspace-write</c> unless granted, and allowed everything unbound. The reach
///     of that today is benign, and was checked against the registered tool surface; the standing obligation it creates is not optional: any new tool
///     source that can write anything must get its own <c>Thalos:ToolPolicies</c> line in the same change,
///     because without one it is silently granted to an unattended agent that loops without a human in the turn.
///     </para>
///     <para>
///     <b>The write grant (phase 2.5).</b> <paramref name="grant"/> is the entry that grants this run's current node
///     workspace writes, from <see cref="WorkspaceWriteGrant.GrantFor"/>, or <see langword="null"/> when it holds none.
///     <see langword="null"/> is the normal state of every ungranted node, so the parameter is nullable but required:
///     every construction says which (rulings R27 and R29). A granted caller also holds
///     <see cref="WorkspaceWritePolicy.WorkspaceWriterRole"/>, which is what the <c>workspace-write</c> binding on
///     <c>workspace__write_*</c> and <c>workspace__edit_*</c> checks. <c>roslyn__apply_*</c> stays on <c>developer</c>,
///     which this caller never passes, until task B9 makes Roslyn run-scoped.
///     </para>
///     <para>
///     <b>The <c>thalos.*</c> claims come from the run row and reviewed config only.</b>
///     <see cref="RunWorkspaceClaims.RunId"/> routes this caller's <c>workspace__*</c> and run-scoped MCP calls to this
///     run's worktree and servers, and <see cref="RunWorkspaceClaims.WriteExtensions"/> narrows what it may write
///     there. Both are built here from <paramref name="run"/> and <paramref name="grant"/>, never copied from a
///     variable or an inbound identity.
///     </para>
/// </remarks>
/// <param name="run">The run these turns belong to.</param>
/// <param name="grant">The granting write entry, or <see langword="null"/> for a node with no write grant.</param>
internal sealed class WorkflowCaller(WorkflowRun run, WriteGrantConfig? grant) : ISecurityContext, IMemoryOwner
{
    /// <summary>
    ///     The run these turns belong to. Carried so a decorator on the runner side — <see cref="ReviewLensRunner"/>
    ///     — can tell a workflow-run turn from a scheduled or chat turn, and can read the run's current node and
    ///     variables. <c>WorkflowNodeDispatcher</c> forwards this object into <c>SubagentRunRequest.Caller</c>
    ///     without inspecting it, so it is the only channel from a dispatch to a runner decorator that does not
    ///     require parsing a formatted string back apart.
    /// </summary>
    public WorkflowRun Run { get; } = run ?? throw new ArgumentNullException(nameof(run));

    /// <inheritdoc />
    public string Id { get; } = $"workflow:{run.Process}:{run.Id}";

    /// <inheritdoc />
    /// <remarks>
    ///     The process, without the run id — stable across every run of this process, unlike <see cref="Id"/>.
    ///     <c>Thalos.Memory.MemoryOwnerResolver.Resolve</c> uses this instead of <see cref="Id"/> for both
    ///     <c>MemoryTools</c> (the <c>memory__*</c> tools) and <c>MemoryContextProvider</c> (auto-recall), so a
    ///     memory a role writes in one run is still readable — under the same owner — by that role in the next.
    /// </remarks>
    public string MemoryOwnerId { get; } = $"workflow:{run.Process}";

    /// <inheritdoc />
    /// <remarks>
    ///     <see langword="true"/> so <c>MemoryTools.RememberAsync</c> pins every memory this caller writes to the
    ///     turn's agent, ignoring the tool's own <c>shared</c> parameter (which defaults to <see langword="true"/>).
    ///     Without this, a memory written under <see cref="MemoryOwnerId"/> lands in the owner-wide
    ///     <c>(owner, null)</c> partition, which every agent running under that owner — implementer and reviewer
    ///     alike — reads by default: exactly the cross-role leak this type exists to prevent. Pinning only takes
    ///     effect when the turn actually has an agent in scope; <c>WorkflowNodeDispatcher</c> always sets one
    ///     (<c>SubagentRunRequest.AgentId</c>, resolved per node from the process definition's <c>agent:</c> name),
    ///     so that condition holds for every workflow-run turn.
    /// </remarks>
    public bool PinMemoriesToAgent => true;

    /// <inheritdoc />
    /// <remarks><c>{workflow}</c>, plus <see cref="WorkspaceWritePolicy.WorkspaceWriterRole"/> only when granted.</remarks>
    public IReadOnlySet<string> Roles { get; } = grant is not null
        ? new HashSet<string>(StringComparer.Ordinal) { "workflow", WorkspaceWritePolicy.WorkspaceWriterRole }
        : new HashSet<string>(StringComparer.Ordinal) { "workflow" };

    /// <inheritdoc />
    /// <remarks>
    ///     <see cref="RunWorkspaceClaims.RunId"/>, <c>node</c> and <c>started_by</c> (empty for a run with no starter),
    ///     and <see cref="RunWorkspaceClaims.WriteExtensions"/> only when granted: the granting entry's extensions,
    ///     lower-cased and joined with <c>';'</c>. The workspace tools intersect it with their host-wide ceiling
    ///     (ruling R29). Absent rather than blank for an ungranted caller, because a present blank claim is a grant
    ///     of zero extensions, not the absence of one.
    /// </remarks>
    public IReadOnlyDictionary<string, string> Claims { get; } = BuildClaims(run, grant);

    private static FrozenDictionary<string, string> BuildClaims(WorkflowRun run, WriteGrantConfig? grant)
    {
        var claims = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [RunWorkspaceClaims.RunId] = run.Id.ToString(),
            ["node"] = run.CurrentNode,
            ["started_by"] = run.StartedBy?.Id ?? "",
        };
        if (grant is not null)
        {
            claims[RunWorkspaceClaims.WriteExtensions] = string.Join(';', grant.AllowedExtensions
                .Where(e => DaedalusAgentsServiceCollectionExtensions.AllowedExtensionPattern().IsMatch(e))
                .Select(AsciiLower));
        }

        return claims.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>
    ///     Lower-cases an extension that already matched the pattern <c>ValidateWorkflowWriteConfig</c> enforces on every
    ///     configured one: a dot, then ASCII letters and digits. The ASCII mapping is exact both ways, unlike a
    ///     culture's. An entry that does not match can only come from a grant built outside that validation, and is left
    ///     out of the claim, so it narrows the grant rather than widening it: a <c>';'</c> inside one entry would
    ///     otherwise split into extra extensions when the tools parse the claim.
    /// </summary>
    private static string AsciiLower(string extension) =>
        string.Create(extension.Length, extension, static (target, source) => Ascii.ToLower(source, target, out _));
}
