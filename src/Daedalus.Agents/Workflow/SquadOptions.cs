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
///     This type does not touch <c>processes/manufacture.yaml</c>, and what it guarantees is narrower than an
///     earlier version of this comment claimed. <see cref="SquadAgentResolver.Resolve"/> returns
///     <see cref="FallbackAgentName"/> for every role when <see cref="Enabled"/> is off — but nothing on the
///     dispatch path calls it, so flipping this flag off does <b>not</b> currently route anything back. Since
///     task B4 the process file names <c>implementer</c> and <c>reviewer</c> directly, so a disabled squad runs
///     the two roles regardless. See <see cref="SquadAgentResolver"/>'s own remarks for why closing that gap is
///     held back to the task that also records the run's mode, rather than done here in half.
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
    ///     real <c>Thalos:Agents</c> entry on the same host — <see cref="SquadAgentResolver"/> does not validate
    ///     that itself, the same way <c>Thalos:Channels:DefaultAgent</c> is pinned only by
    ///     <c>DefaultAgentConfigurationTests</c> rather than by a startup check.
    /// </summary>
    public string FallbackAgentName { get; set; } = "";
}
