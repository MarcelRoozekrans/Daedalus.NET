using Daedalus.Agents.Security;
using Thalos.Workflow;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     Decides whether a run's current node may write into the run's workspace. Every input is host-controlled: the
///     reviewed <c>Thalos:Workflow:WriteGrants</c> config, and the run row's write-once <see cref="WorkflowRun.StartedBy"/>
///     and <see cref="WorkflowRun.Manifest"/>. Nothing a model or a run variable can set takes part.
/// </summary>
internal static class WorkspaceWriteGrant
{
    /// <summary>
    ///     The granting entry, or <see langword="null"/> when the run does not qualify. All three must hold: the
    ///     <c>(process, node)</c> pair is configured; the run's starter holds <c>developer</c> or <c>admin</c>; the node
    ///     is a task node, pinned in the manifest. A run with no starter or no manifest, which is every run from before
    ///     phase 2.5, never qualifies. The entry rather than a bool, so its <see cref="WriteGrantConfig.AllowedExtensions"/>
    ///     reach the workspace tools (ruling R29); a <see langword="null"/> list, allowed only under the run sandbox (S6),
    ///     reaches them as no write-extensions claim at all.
    /// </summary>
    public static WriteGrantConfig? GrantFor(IEnumerable<WriteGrantConfig> grants, WorkflowRun run)
    {
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(run);

        return run.StartedBy is { } by
               && (by.Roles.Contains(DeveloperPolicy.DeveloperRole, StringComparer.Ordinal)
                   || by.Roles.Contains(DeveloperPolicy.AdminRole, StringComparer.Ordinal))
               && run.Manifest?.Nodes.ContainsKey(run.CurrentNode) == true
            ? grants.FirstOrDefault(g =>
                string.Equals(g.Process, run.Process, StringComparison.Ordinal)
                && string.Equals(g.Node, run.CurrentNode, StringComparison.Ordinal))
            : null;
    }
}
