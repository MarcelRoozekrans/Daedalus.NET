namespace Daedalus.Agents.Workflow;

/// <summary>
///     Resolves a workflow process node's declared role (<c>implementer</c>, <c>reviewer</c>, …) to the agent
///     name a dispatch should actually run against. Exists so <c>processes/manufacture.yaml</c> can name roles
///     once and stay untouched whether or not <see cref="SquadOptions.Enabled"/> is on: this type, not the
///     process file, is what decides whether a role gets its own agent or all roles collapse onto one.
/// </summary>
/// <remarks>
///     <b>Wired into dispatch since task B5, together with the half that makes it honest.</b>
///     <see cref="SquadWorkflowReferenceResolver"/> puts every process-node <c>agent:</c> name through
///     <see cref="Resolve"/> before the agent catalog is consulted, so this type is what
///     <c>Thalos:Squad:Enabled</c> actually changes. Task B2 built it unwired; task B4 rewired
///     <c>processes/manufacture.yaml</c> onto the role names and <em>deliberately</em> still did not wire this,
///     leaving a window in which a disabled flag rolled nothing back.
///     <para>
///     B4's reasoning for waiting, which is still the reason both halves are one change: design section 7
///     requires the squad-off fallback to be <em>loud</em>. With one agent implementing and reviewing its own
///     work, the run record has to say so, or a <c>Succeeded</c> run under a disabled squad is indistinguishable
///     from one with genuine independent review — the exact false assurance this phase exists to remove.
///     <see cref="WorkflowRunModeStore"/> is that half: it writes the mode onto every transition the run
///     records, in <c>workflow_run_event</c> rather than only in a log.
///     </para>
///     <para>
///     This type validates nothing about the name it returns. A role that resolves to an agent no host declares
///     fails at <c>ProcessValidator</c>, at load, because <see cref="SquadWorkflowReferenceResolver"/> wraps the
///     one lookup both validation and dispatch share — never per node on a live, paying run.
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
