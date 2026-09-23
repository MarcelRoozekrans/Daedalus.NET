namespace Daedalus.Agents.Workflow;

/// <summary>
///     Resolves a workflow process node's declared role (<c>implementer</c>, <c>reviewer</c>, …) to the agent
///     name a dispatch should actually run against. Exists so <c>processes/manufacture.yaml</c> can name roles
///     once and stay untouched whether or not <see cref="SquadOptions.Enabled"/> is on: this type, not the
///     process file, is what decides whether a role gets its own agent or all roles collapse onto one.
/// </summary>
/// <remarks>
///     <b>Still not wired into dispatch, and the consequence changed in task B4.</b> Nothing calls
///     <see cref="Resolve"/> on the dispatch path: <c>WorkflowNodeDispatcher</c> resolves a node's <c>agent:</c>
///     name straight through <c>IWorkflowReferenceResolver</c>. B2's note here named task B4 as the one that
///     would change that; B4 deliberately did not.
///     <para>
///     What did change in B4 is that <c>processes/manufacture.yaml</c> now names <c>implementer</c> and
///     <c>reviewer</c> where it used to name <c>Daedalus Architect</c> on every node. So until something calls
///     this type, <b><c>Thalos:Squad:Enabled = false</c> no longer rolls the pipeline back</b> — the process file
///     names the roles directly and a disabled flag changes nothing about which agents run.
///     </para>
///     <para>
///     Wiring resolution here alone would be worse than leaving it, which is why B4 left it. Design section 7
///     requires the squad-off fallback to be <em>loud</em>: with one agent implementing and reviewing its own
///     work, the run record has to say so, or a <c>Succeeded</c> run under a disabled squad is indistinguishable
///     from one with genuine independent review — the exact false assurance this phase exists to remove.
///     Collapsing the roles without writing that event would manufacture that condition rather than fix it. Both
///     halves belong to the task that records the run's mode.
///     </para>
/// </remarks>
public sealed class SquadAgentResolver(SquadOptions options)
{
    private readonly SquadOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    ///     Resolves <paramref name="roleName"/> to the agent name a dispatch should use: itself when the squad is
    ///     enabled, or <see cref="SquadOptions.FallbackAgentName"/> for every role when it is not.
    /// </summary>
    /// <param name="roleName">The role a process node declares (<c>implementer</c>, <c>reviewer</c>, …).</param>
    public string Resolve(string roleName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roleName);
        return _options.Enabled ? roleName : _options.FallbackAgentName;
    }
}
