using ZeroAlloc.Results;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     The one seam both <c>WorkflowRunsController</c> and <see cref="Tools.DaedalusManufactureTools"/> use to
///     start a new <c>manufacture</c> run — never <see cref="Thalos.Workflow.WorkflowRunStarter"/> directly, so
///     REST and the <c>manufacture__start</c> tool cannot drift apart on what a run's opening variable or pinned
///     document is called.
/// </summary>
/// <remarks>
///     <b>Always registered.</b> Every host that calls <c>DaedalusAgentsServiceCollectionExtensions.AddDaedalusAgents</c>
///     resolves an implementation, whether or not <c>Thalos:Workflow:Enabled</c> is true. A host with the engine off
///     gets <see cref="DisabledManufactureRunStarter"/>, which reports that plainly instead of failing DI resolution —
///     the Integration test suite boots with the engine disabled on every host but one, and both the controller and
///     the tool need something to resolve regardless.
/// </remarks>
public interface IManufactureRunStarter
{
    /// <summary>
    ///     Starts one manufacture run with <paramref name="workIntent"/> as its opening variable
    ///     (<c>work_intent</c>) and the current standing instructions pinned as a manifest document
    ///     (<see cref="ManufactureRunStarter.StandingInstructionsDocument"/>). Failure text is safe to show a
    ///     caller — a blank or over-long <paramref name="workIntent"/>, a disabled engine, and a pin failure (an
    ///     unresolvable agent or an inactive skill on a task node) are all reported through
    ///     <see cref="Result{T}.Error"/> rather than thrown.
    /// </summary>
    ValueTask<Result<Guid>> StartAsync(string workIntent, CancellationToken ct);
}
