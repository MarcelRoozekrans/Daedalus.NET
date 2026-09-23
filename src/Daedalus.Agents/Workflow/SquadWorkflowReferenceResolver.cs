using Microsoft.Extensions.Logging;
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
internal sealed partial class SquadWorkflowReferenceResolver(
    IWorkflowReferenceResolver inner,
    SquadAgentResolver squad,
    ILogger<SquadWorkflowReferenceResolver> logger) : IWorkflowReferenceResolver
{
    private readonly IWorkflowReferenceResolver _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly SquadAgentResolver _squad = squad ?? throw new ArgumentNullException(nameof(squad));
    private readonly ILogger<SquadWorkflowReferenceResolver> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    /// <remarks>
    ///     The name is mapped before the catalog is consulted, so with the squad off the catalog is never asked
    ///     about <c>implementer</c> or <c>reviewer</c> at all. <see cref="SquadAgentResolver.Resolve"/> is
    ///     idempotent — it maps the fallback name to itself in both modes — so a node that already names the
    ///     fallback agent, as <c>publish</c> does, resolves identically either way.
    ///     <para>
    ///     <b>A miss is logged with the name that was actually looked up.</b> Everything downstream of this
    ///     method only ever sees the <em>declared</em> role: <c>ProcessValidator</c> reports the name it read
    ///     out of the process file, and <c>WorkflowNodeDispatcher.ResolveAgentAsync</c> fails the run naming
    ///     <c>ProcessNode.Agent</c>. When the squad has remapped the name, both of those point a reader at
    ///     <c>processes/manufacture.yaml</c> for a fault that lives in <c>Thalos:Squad:FallbackAgentName</c> —
    ///     and the file they are sent to is correct, so the search ends nowhere. This is the only place that
    ///     holds both names at once, so it is the only place that can say which one missed.
    ///     </para>
    /// </remarks>
    public async ValueTask<AgentId?> ResolveAgentIdAsync(string name, CancellationToken ct)
    {
        var resolved = _squad.Resolve(name);
        var id = await _inner.ResolveAgentIdAsync(resolved, ct).ConfigureAwait(false);

        // Only when the squad actually remapped the name. An unmapped miss is already reported accurately by
        // the caller, and logging it again here would be noise that says nothing the run's own error does not.
        if (id is null && !string.Equals(resolved, name, StringComparison.Ordinal))
        {
            LogFallbackAgentMissing(_logger, name, resolved);
        }

        return id;
    }

    /// <inheritdoc />
    public ValueTask<bool> SkillExistsAsync(string name, CancellationToken ct) => _inner.SkillExistsAsync(name, ct);

    [LoggerMessage(
        EventId = 2310,
        Level = LogLevel.Error,
        Message = "Workflow role '{DeclaredRole}' was mapped to agent '{ResolvedAgent}' by Thalos:Squad, and no agent of that name exists. " +
                  "The failure reported against this run names the declared role, not the agent that was looked up: check " +
                  "Thalos:Squad:FallbackAgentName against Thalos:Agents, not the process file.")]
    private static partial void LogFallbackAgentMissing(ILogger logger, string declaredRole, string resolvedAgent);
}
