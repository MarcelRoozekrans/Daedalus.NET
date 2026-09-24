using System.ComponentModel;
using Daedalus.Agents.Workflow;
using Thalos;

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

    /// <summary>Starts a new manufacture run for a work intent, reporting the run id or why it could not start.</summary>
    [ThalosTool(StartToolName)]
    [Description(
        "Start a new manufacture run: implement, review and publish a change for the given work intent. Returns " +
        "the started run's id, or explains why the run could not start — a blank or over-long work intent, the " +
        "workflow engine being disabled on this host, or a task node's agent or skill failing to resolve.")]
    public async Task<string> Start(
        [Description("What the run should manufacture, in the requester's own words.")] string workIntent,
        CancellationToken ct = default)
    {
        var result = await starter.StartAsync(workIntent, ct).ConfigureAwait(false);
        return result.IsSuccess
            ? $"Started manufacture run {result.Value}."
            : $"Could not start a manufacture run: {result.Error}";
    }
}
