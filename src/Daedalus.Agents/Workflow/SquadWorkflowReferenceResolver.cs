using Thalos;
using Thalos.Workflow;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     The <see cref="IWorkflowReferenceResolver"/> every workflow agent name goes through on this host: it
///     passes the name to <see cref="SquadAgentResolver.Resolve"/> first, so a process file naming
///     <c>implementer</c> and <c>reviewer</c> runs as those two agents when <c>Thalos:Squad:Enabled</c> is on
///     and collapses onto <see cref="SquadOptions.FallbackAgentName"/> for every node when it is off.
/// </summary>
/// <remarks>
///     <b>This is the wiring task B2 built <see cref="SquadAgentResolver"/> for and task B4 deliberately left
///     undone.</b> B4's reasoning was that collapsing the roles without also recording the collapse in the run
///     would manufacture the false assurance design section 7 exists to remove — one agent implementing and
///     reviewing its own work, in a run whose record reads exactly like one with independent review. Both
///     halves land together: this type collapses the roles, and <see cref="WorkflowRunModeStore"/> writes the
///     mode onto every transition the run records.
///     <para>
///     <b>Why here and not inside <c>WorkflowNodeDispatcherFactory</c>.</b> This resolver is the single lookup
///     Thalos deliberately shares between <c>ProcessValidator</c> at load time and
///     <see cref="WorkflowNodeDispatcher"/> at dispatch time — see <see cref="IWorkflowReferenceResolver.ResolveAgentIdAsync"/>'s
///     own remarks on why there is no separate existence check. Decorating only the dispatcher's copy would
///     rebuild exactly the split that interface exists to prevent: a process would validate against the
///     declared role names and then dispatch against different ones. It would also break the rollback D3 asks
///     for — a host that turns the squad off and drops the two roster agents from <c>Thalos:Agents</c> would
///     fail validation on names nothing would ever have dispatched.
///     </para>
///     <para>
///     <b>Skill names are not remapped.</b> <see cref="SkillExistsAsync"/> delegates untouched: a node's
///     <c>skill:</c> pin names a document in <c>skills/</c>, not an agent, and the squad flag has no opinion
///     about it. The fallback agent must therefore be configured with every skill the roster agents' nodes pin,
///     or a squad-off run fails at the node whose skill its agent cannot load — which is what
///     <c>ProcessNodeSkillAllowlistTests</c> checks against the shipped configuration.
///     </para>
/// </remarks>
internal sealed class SquadWorkflowReferenceResolver(IWorkflowReferenceResolver inner, SquadAgentResolver squad)
    : IWorkflowReferenceResolver
{
    private readonly IWorkflowReferenceResolver _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly SquadAgentResolver _squad = squad ?? throw new ArgumentNullException(nameof(squad));

    /// <inheritdoc />
    /// <remarks>
    ///     The name is mapped before the catalog is consulted, so with the squad off the catalog is never asked
    ///     about <c>implementer</c> or <c>reviewer</c> at all. <see cref="SquadAgentResolver.Resolve"/> is
    ///     idempotent — it maps the fallback name to itself in both modes — so a node that already names the
    ///     fallback agent, as <c>publish</c> does, resolves identically either way.
    /// </remarks>
    public ValueTask<AgentId?> ResolveAgentIdAsync(string name, CancellationToken ct) =>
        _inner.ResolveAgentIdAsync(_squad.Resolve(name), ct);

    /// <inheritdoc />
    public ValueTask<bool> SkillExistsAsync(string name, CancellationToken ct) => _inner.SkillExistsAsync(name, ct);
}
