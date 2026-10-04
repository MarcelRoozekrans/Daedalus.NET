using ZeroAlloc.Results;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     The one seam both <c>WorkflowRunsController</c> and <see cref="Tools.DaedalusManufactureTools"/> use to
///     start a new <c>manufacture</c> run — never <see cref="Thalos.Workflow.WorkflowRunStarter"/> directly, so
///     REST and the <c>manufacture__start</c> tool cannot drift apart on what a run's opening variables or pinned
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
    ///     Starts one manufacture run on the allow-listed repository <paramref name="request"/> names, in a git
    ///     worktree of its own, with <see cref="ManufactureStartRequest.WorkIntent"/> as its opening variable
    ///     (<c>work_intent</c>), beside the host's own <c>run_mode</c> (see <see cref="RunMode"/>), and pinned, together with
    ///     the worktree's standing instructions, as manifest
    ///     documents (<see cref="ManufactureRunStarter.WorkIntentDocument"/> and
    ///     <see cref="ManufactureRunStarter.StandingInstructionsDocument"/>). The run records
    ///     <see cref="ManufactureStartRequest.StartedBy"/> as the principal that started it. Failure text is safe
    ///     to show a caller — a blank or over-long work intent, a repository that is not allow-listed, a worktree
    ///     that could not be prepared, a disabled engine, and a pin failure (an unresolvable agent or an inactive
    ///     skill on a task node) are all reported through <see cref="Result{T,E}.Error"/> as a <see cref="ManufactureStartFailure"/> whose kind says
    ///     whose fault it is, rather than thrown.
    /// </summary>
    ValueTask<Result<Guid, ManufactureStartFailure>> StartAsync(ManufactureStartRequest request, CancellationToken ct);
}

/// <summary>Why a manufacture start failed, in terms a caller maps to a response: see <see cref="ManufactureStartFailureKind"/>.</summary>
/// <param name="Kind">Whose fault the failure is.</param>
/// <param name="Message">Text that is safe to show a caller.</param>
public sealed record ManufactureStartFailure(ManufactureStartFailureKind Kind, string Message);

/// <summary>Whose fault a failed manufacture start is.</summary>
public enum ManufactureStartFailureKind
{
    /// <summary>The request itself is refused: a blank or over-long intent, a repository that is not allow-listed, or a workspace the provider rejected as invalid.</summary>
    Invalid,

    /// <summary>The runtime a start needs is transiently unreachable, such as the sandbox engine being down, so a retry later may succeed. The endpoint answers 503 with Retry-After.</summary>
    Unavailable,

    /// <summary>The workflow engine is switched off by a host setting that no retry gets past. The endpoint answers 503 without Retry-After.</summary>
    Disabled,

    /// <summary>
    ///     The workflow run starter refused the start with one of its fixed refusals: no active version of the process, a
    ///     definition that does not load, a task node that cannot be pinned such as one whose skill is deactivated, or a
    ///     caller-input check of the store. The message names the node. The endpoint answers 422; real outages throw
    ///     instead of arriving here.
    /// </summary>
    Unstartable,

    /// <summary>Anything else: the request was fine and the host failed it with an error code that is neither a validation nor a provider error. The endpoint answers 500.</summary>
    Failed,
}
