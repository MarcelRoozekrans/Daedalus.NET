using Thalos.Workflow;
using ZeroAlloc.Results;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     The one seam <c>Daedalus.Api</c>'s resume/cancel REST endpoints use to reach <see cref="IWorkflowStore"/> —
///     never an agent, never a Thalos tool. Exists to fix one thing <see cref="IWorkflowStore.ResumeAsync"/> does
///     not: its own implementation (<c>Thalos.Workflow.Orm.OrmWorkflowStore</c>) rejects a mismatched signal with
///     <c>$"Workflow run '{runId}' is not awaiting signal '{signal}'."</c> — <c>signal</c> there is the caller's
///     wrong value, so the message never names what the run is actually waiting for. <c>ZeroAlloc.Saga</c>
///     shipped with exactly this shape of silence: an unexpected event type was dropped with no error anywhere,
///     leaving a saga stuck at its first step forever. This type reads the run first and, on a mismatch, fails
///     with a message naming <see cref="WorkflowRun.AwaitingSignal"/> before ever calling the store.
/// </summary>
/// <remarks>
///     <para>
///     <b>This does not route.</b> A matching signal is handed straight to <see cref="IWorkflowStore.ResumeAsync"/>,
///     which still verifies the match itself, still calls <c>WorkflowInterpreter.Advance</c> with the run's status
///     left at <see cref="WorkflowStatus.Awaiting"/>, and still applies whatever transition comes back. Only the
///     caller-facing message for the mismatch case is this type's own.
///     </para>
///     <para>
///     <b>Not the security boundary.</b> Nothing here checks who is calling. <c>WorkflowRunsController</c> is
///     reachable only once ASP.NET Core's own <c>WorkflowResume</c> authorization policy (<c>developer</c> or
///     <c>admin</c> role, see <c>Program.cs</c>) has already let the request through — the same criterion
///     <c>Thalos:ToolPolicies</c> binds <c>git__*</c> and <c>repoaction__*</c> to, enforced here by ASP.NET Core's
///     role-based authorization instead of <c>DefaultToolAuthorizer</c>, because a REST endpoint is not a Thalos
///     tool call and <c>DefaultToolAuthorizer</c> never sees one.
///     </para>
/// </remarks>
public sealed class WorkflowRunGateway(IWorkflowStore store)
{
    private readonly IWorkflowStore _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>Finds a run by id, or <see langword="null"/> if none exists.</summary>
    public ValueTask<WorkflowRun?> FindAsync(Guid runId, CancellationToken ct) => _store.FindAsync(runId, ct);

    /// <summary>
    ///     Resumes <paramref name="runId"/> if it is parked awaiting exactly <paramref name="signal"/>. A run not
    ///     found, not awaiting anything, or awaiting a different signal fails loudly — the error names what the
    ///     run is actually awaiting rather than only echoing back the signal the caller supplied — and the run's
    ///     status is left untouched: no silent no-op, no partial transition.
    /// </summary>
    public async ValueTask<Result> ResumeAsync(Guid runId, string signal, string? payload, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signal);

        var run = await _store.FindAsync(runId, ct).ConfigureAwait(false);
        if (run is null)
        {
            return Result.Failure($"Workflow run '{runId}' was not found.");
        }

        if (run.Status != WorkflowStatus.Awaiting)
        {
            return Result.Failure($"Workflow run '{runId}' is not awaiting any signal (status: {run.Status}).");
        }

        if (!string.Equals(run.AwaitingSignal, signal, StringComparison.Ordinal))
        {
            return Result.Failure(
                $"Workflow run '{runId}' is awaiting signal '{run.AwaitingSignal}', not '{signal}'.");
        }

        return await _store.ResumeAsync(runId, signal, payload, ct).ConfigureAwait(false);
    }

    /// <summary>Cancels <paramref name="runId"/> for <paramref name="reason"/>. A no-op past a terminal status.</summary>
    public ValueTask CancelAsync(Guid runId, string reason, CancellationToken ct) => _store.CancelAsync(runId, reason, ct);
}
