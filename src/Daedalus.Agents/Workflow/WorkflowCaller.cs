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
