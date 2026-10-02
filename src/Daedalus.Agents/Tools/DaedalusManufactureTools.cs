using System.ComponentModel;
using Daedalus.Agents.Security;
using Daedalus.Agents.Workflow;
using Thalos;
using ZeroAlloc.Authorization;

namespace Daedalus.Agents.Tools;

/// <summary>
///     Starts a <c>manufacture</c> run from an agent turn: <c>manufacture__start</c>. The only tool this source
///     exposes — the source itself, not just this one tool, is bound to <see cref="Security.DeveloperPolicy"/> in
///     <c>Thalos:ToolPolicies</c> (<c>manufacture__*</c> → <c>developer</c>), because starting a run kicks off
///     unattended work that spends real API tokens with no human turn watching it: an agent that could start a run
///     for itself, from a scheduled or workflow-run turn, could loop that spend indefinitely. See
///     <c>DaedalusAgentsServiceCollectionExtensions.ManufactureToolSourceName</c> for why <c>manufacture</c> and
///     not <c>workflow</c> — the latter is reserved so that name can never collide with a real tool source, which
///     is what <c>ResumeToolBoundaryTests.No_tool_source_is_named_workflow</c> guards.
/// </summary>
/// <param name="starter">
///     The one seam this tool and <c>WorkflowRunsController</c> share for starting a run — see that interface's
///     own remarks for why an always-registered default exists.
/// </param>
[ThalosToolType]
public sealed class DaedalusManufactureTools(IManufactureRunStarter starter)
{
    /// <summary>The unqualified tool name; agents see it as <c>manufacture__start</c>.</summary>
    public const string StartToolName = "start";

    /// <summary>
    ///     Starts a new manufacture run for a work intent on an allow-listed repository, reporting the run id or why it
    ///     could not start.
    /// </summary>
    /// <param name="caller">
    ///     The calling turn's principal, bound by Thalos because the parameter is typed exactly
    ///     <see cref="ISecurityContext"/>; it never appears in the tool's schema. The run records it as its starter.
    /// </param>
    /// <param name="workIntent">What the run should manufacture.</param>
    /// <param name="repository">
    ///     The allow-listed repository name to change. Only a name: the remote comes from reviewed configuration.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    [ThalosTool(StartToolName)]
    [Description(
        "Start a new manufacture run on an allow-listed repository: implement and review a change for the given work " +
        "intent, then publish it after a human approves. Returns the started run's id, or explains why the run could " +
        "not start — a blank or over-long work intent, a repository that is not allow-listed, the workflow engine being " +
        "disabled on this host, or a task node's agent or skill failing to resolve.")]
    public async Task<string> Start(
        ISecurityContext caller,
        [Description("What the run should manufacture, in the requester's own words.")] string workIntent,
        [Description("The allow-listed repository name to change, e.g. \"sandbox\".")] string repository,
        CancellationToken ct = default)
    {
        var result = await starter.StartAsync(
                new ManufactureStartRequest(workIntent, repository, RunPrincipals.From(caller)), ct)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? $"Started manufacture run {result.Value}."
            : $"Could not start a manufacture run: {result.Error.Message}";
    }
}
