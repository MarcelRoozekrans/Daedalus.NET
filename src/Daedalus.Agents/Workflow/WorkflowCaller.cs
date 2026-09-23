using System.Collections.Frozen;
using Thalos.Memory;
using Thalos.Workflow;
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
///     <c>developer</c> and allowed everything unbound. The reach of that today is benign, and was checked
///     against the registered tool surface; the standing obligation it creates is not optional: any new tool
///     source that can write anything must get its own <c>Thalos:ToolPolicies</c> line in the same change,
///     because without one it is silently granted to an unattended agent that loops without a human in the turn.
///     </para>
/// </remarks>
internal sealed class WorkflowCaller(WorkflowRun run) : ISecurityContext, IMemoryOwner
{
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
    public IReadOnlySet<string> Roles { get; } = new HashSet<string>(StringComparer.Ordinal) { "workflow" };

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> Claims { get; } = FrozenDictionary<string, string>.Empty;
}
