using System.Collections.Frozen;
using Thalos.Workflow;
using ZeroAlloc.Authorization;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     The <see cref="ISecurityContext"/> a workflow run's agent turns execute as. A run has no human behind it,
///     so its identity is derived from the run itself rather than borrowed from whoever started it — the same
///     reasoning <see cref="Daedalus.Agents.Scheduling.DetachedPrincipal"/> gives for scheduled runs: an ambient
///     principal's access could change hours into a long-lived run with no live turn around to notice.
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
internal sealed class WorkflowCaller(WorkflowRun run) : ISecurityContext
{
    /// <inheritdoc />
    public string Id { get; } = $"workflow:{run.Process}:{run.Id}";

    /// <inheritdoc />
    public IReadOnlySet<string> Roles { get; } = new HashSet<string>(StringComparer.Ordinal) { "workflow" };

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> Claims { get; } = FrozenDictionary<string, string>.Empty;
}
