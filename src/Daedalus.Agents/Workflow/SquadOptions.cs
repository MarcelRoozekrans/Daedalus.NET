namespace Daedalus.Agents.Workflow;

/// <summary>
///     <c>Thalos:Squad</c>: whether phase 2.3's manufacturing squad (independent <c>implementer</c> and
///     <c>reviewer</c> roles) actually runs as two agents, and which single agent every role falls back to when it
///     does not. Bound onto <see cref="DaedalusAgentsOptions.Squad"/> the same way <c>Thalos:Workflow</c> binds
///     onto <see cref="WorkflowConfig"/> — a property on <see cref="DaedalusAgentsOptions"/> populated by the one
///     <c>configuration.GetSection(DaedalusAgentsOptions.SectionName).Bind(options)</c> call in
///     <c>AddDaedalusAgents</c>, not a second, independent bind.
/// </summary>
/// <remarks>
///     This type does not touch <c>processes/manufacture.yaml</c>: the process file names roles, and this flag
///     decides what those names resolve to. Turning it off routes every node to <see cref="FallbackAgentName"/>
///     through <see cref="SquadWorkflowReferenceResolver"/>, which wraps the single agent-name lookup both
///     <c>ProcessValidator</c> and <c>WorkflowNodeDispatcher</c> use — so a rollback needs no edit to the
///     process file and cannot leave validation and dispatch disagreeing about which agents a run uses.
///     <para>
///     <b>A disabled squad is a genuinely weaker configuration, and the run says so.</b> One agent implements
///     and reviews its own work, and <see cref="WorkflowRunModeStore"/> writes that onto every transition the
///     run records. Turning this off is a rollback, not a free switch.
///     </para>
/// </remarks>
public sealed class SquadOptions
{
    /// <summary>Configuration section name: <c>Thalos:Squad</c>.</summary>
    public const string SectionName = "Thalos:Squad";

    /// <summary>
    ///     Whether a workflow role resolves to its own agent. Defaults to <see langword="false"/>: a role split
    ///     this new stays opt-in per host rather than silently on the moment <c>Thalos:Agents</c> happens to
    ///     declare an <c>implementer</c>/<c>reviewer</c> pair. <c>Daedalus.Api/appsettings.json</c> sets this
    ///     <see langword="true"/> explicitly — it is the host that runs manufacturing — and
    ///     <c>Daedalus.Cli/appsettings.json</c> sets it <see langword="false"/> explicitly, matching the
    ///     precedent <c>Thalos:Workflow:Enabled</c> set in the preceding phase.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    ///     Agent name every role resolves to when <see cref="Enabled"/> is <see langword="false"/>. Must name a
    ///     real <c>Thalos:Agents</c> entry on the same host.
    /// </summary>
    /// <remarks>
    ///     <b>Blank is rejected at startup, in both modes.</b> <c>AddDaedalusAgents</c>' <c>ValidateSquadConfig</c>
    ///     throws on a blank value beside the <c>Thalos:Memory</c> and <c>Thalos:Skills</c> checks — see that
    ///     method for why a blank name is an <see cref="ArgumentException"/> out of Thalos' own resolver rather
    ///     than a clean "no such agent", and why deleting this whole section is the realistic way to reach it.
    ///     <see cref="SquadAgentResolver"/> still does not validate the name it returns: that the name exists is
    ///     checked by <c>ProcessValidator</c> at load through <see cref="SquadWorkflowReferenceResolver"/>, which
    ///     is the one lookup validation and dispatch share.
    /// </remarks>
    public string FallbackAgentName { get; set; } = "";
}
