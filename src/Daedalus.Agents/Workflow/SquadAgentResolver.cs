namespace Daedalus.Agents.Workflow;

/// <summary>
///     Resolves a workflow process node's declared role (<c>implementer</c>, <c>reviewer</c>, …) to the agent
///     name a dispatch should actually run against. Exists so <c>processes/manufacture.yaml</c> can name roles
///     once and stay untouched whether or not <see cref="SquadOptions.Enabled"/> is on: this type, not the
///     process file, is what decides whether a role gets its own agent or all roles collapse onto one.
/// </summary>
/// <remarks>
///     <b>Not wired into dispatch yet.</b> This task only creates the resolver and its flag; a later task
///     (B4) is what makes <c>WorkflowNodeDispatcher</c> call <see cref="Resolve"/> for a node's agent reference
///     instead of using the role name directly.
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
