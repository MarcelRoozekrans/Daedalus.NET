using Daedalus.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
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
///     <para>
///     <b><see cref="StandingInstructionsWriter"/> defaults to <see langword="null"/>; the run's history and its
///     record store do not.</b> The writer's default keeps the resume tests in <c>ResumeBoundaryTests.cs</c> free of
///     a writer they never use, while every host still wires the real one through DI (see
///     <c>AddDaedalusWorkflow</c>). A caller that asks the public
///     <see cref="ResumeAsync(Guid,string,string?,bool,RunPrincipal,CancellationToken)"/> overload to apply standing
///     instructions without one throws: that combination is a wiring bug, never a normal outcome a caller should
///     branch on. <paramref name="history"/> and <paramref name="scopes"/> are required, not defaulted (task B15,
///     ruling R25), so no host can build a gateway whose run view silently shows no usage and no write audit.
///     </para>
/// </remarks>
/// <param name="store">The workflow store every read and write of the run itself goes through.</param>
/// <param name="history">Reads the run's event log back, including each completed node's token usage.</param>
/// <param name="scopes">Creates the scope each read of the run's host-written records resolves <see cref="IWorkflowRunRecordStore"/> from.</param>
/// <param name="writer">Writes an approved standing-instructions proposal; see the remarks.</param>
public sealed class WorkflowRunGateway(
    IWorkflowStore store, IWorkflowRunHistory history, IServiceScopeFactory scopes, StandingInstructionsWriter? writer = null)
{
    private readonly IWorkflowStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IWorkflowRunHistory _history = history ?? throw new ArgumentNullException(nameof(history));
    private readonly IServiceScopeFactory _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
    private readonly StandingInstructionsWriter? _writer = writer;

    /// <summary>Finds a run by id, or <see langword="null"/> if none exists.</summary>
    public ValueTask<WorkflowRun?> FindAsync(Guid runId, CancellationToken ct) => _store.FindAsync(runId, ct);

    /// <summary>
    ///     Every event recorded for <paramref name="runId"/>, in ascending seq order, as <see cref="IWorkflowRunHistory"/>
    ///     reads them. A completion event carries the usage of the node it closes.
    /// </summary>
    public ValueTask<IReadOnlyList<WorkflowRunEvent>> ListEventsAsync(Guid runId, CancellationToken ct) =>
        _history.ListEventsAsync(runId, ct);

    /// <summary>
    ///     The host-written records of <paramref name="runId"/>, only those of <paramref name="kind"/> when it is not
    ///     null, in the order <see cref="IWorkflowRunRecordStore.ListAsync"/> gives: by seq, then append order.
    /// </summary>
    public async ValueTask<IReadOnlyList<WorkflowRunRecord>> ListRecordsAsync(Guid runId, string? kind, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IWorkflowRunRecordStore>()
            .ListAsync(runId, kind, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Resumes <paramref name="runId"/> if it is parked awaiting exactly <paramref name="signal"/>. A run not
    ///     found, not awaiting anything, or awaiting a different signal fails loudly — the error names what the
    ///     run is actually awaiting rather than only echoing back the signal the caller supplied — and the run's
    ///     status is left untouched: no silent no-op, no partial transition.
    /// </summary>
    /// <remarks>
    ///     <c>internal</c>, not <c>public</c>: fix round 1 of task B5 found nothing in <c>src</c> calling this
    ///     overload directly any more — <c>Daedalus.Api.Controllers.WorkflowRunsController.Resume</c> only ever calls the
    ///     public <see cref="ResumeAsync(Guid,string,string?,bool,RunPrincipal,CancellationToken)"/> overload below,
    ///     which applies standing instructions when asked before delegating here. A future <c>public</c> caller of
    ///     this overload could bypass that entirely — resuming a gate with no chance to apply a proposal, or worse,
    ///     no chance to be refused when it should have been. <c>internal</c> keeps this callable only from within
    ///     <c>Daedalus.Agents</c> and from <c>Daedalus.Tests.Integration</c>/<c>Daedalus.Tests.Unit</c> (both
    ///     granted <c>InternalsVisibleTo</c>) — <c>ResumeSignalMismatchTests</c> in <c>ResumeBoundaryTests.cs</c>
    ///     still constructs <see cref="WorkflowRunGateway"/> directly and calls this overload.
    ///     <para>
    ///     <b>Every resume names its approver.</b> <paramref name="resumedBy"/> is required and never null: the store
    ///     records it on the run as <see cref="WorkflowRun.LastResume"/>, so who approved a gate is part of the
    ///     run. There is no overload that resumes without one.
    ///     </para>
    /// </remarks>
    internal async ValueTask<Result> ResumeAsync(Guid runId, string signal, string? payload, RunPrincipal resumedBy, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signal);
        ArgumentNullException.ThrowIfNull(resumedBy);

        var run = await _store.FindAsync(runId, ct).ConfigureAwait(false);
        if (run is null)
        {
            return Result.Failure($"Workflow run '{runId}' was not found.");
        }

        var awaiting = CheckAwaiting(run, signal, runId);
        if (awaiting.IsFailure)
        {
            return awaiting;
        }

        return await _store.ResumeAsync(
            runId, new WorkflowResumeRequest { Signal = signal, Payload = payload, ResumedBy = resumedBy }, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Task B5: the resume path a human's <c>POST /api/workflow-runs/{id}/resume</c> actually calls.
    ///     <paramref name="applyStandingInstructions"/> is never set except by an explicit choice in that request
    ///     body — see <see cref="StandingInstructionsWriter"/>'s own remarks for why that is this design's trust
    ///     boundary. When set: the run is read and checked against <paramref name="signal"/> with the same
    ///     <see cref="CheckAwaiting"/> rule <see cref="ResumeAsync(Guid,string,string?,RunPrincipal,CancellationToken)"/> uses
    ///     for its own check below — fix round 1 of this task found the original code skipped this check here,
    ///     so a wrong signal or an already-cancelled/succeeded run would still write the file before the engine
    ///     resume eventually refused it. Only once that check passes is
    ///     <see cref="StandingInstructionsWriter.ApplyAsync"/> called, and on any failure it is returned
    ///     unchanged — <see cref="ResumeAsync(Guid,string,string?,RunPrincipal,CancellationToken)"/> is never reached, so a
    ///     stale or missing proposal, a wrong signal, or a run that is not awaiting can never leave the run's
    ///     status touched. Only once the write (or the no-op skip, when the flag is unset) succeeds does the
    ///     engine resume run, and its own failure maps to <see cref="ResumeRefusal.EngineRefused"/> here, with
    ///     that failure's message carried through as <see cref="ResumeFailure.Detail"/> unchanged.
    ///     <para>
    ///     <b>The write is not rolled back.</b> If <see cref="StandingInstructionsWriter.ApplyAsync"/> succeeds
    ///     and the engine resume that follows then loses a concurrency race
    ///     (<see cref="WorkflowConcurrencyException"/> — another writer changed the run between this method's own
    ///     pre-check and the store's write), the file has already been written and stays written; this method
    ///     catches that exception and reports it as <see cref="ResumeRefusal.EngineRefused"/>, naming in
    ///     <see cref="ResumeFailure.Detail"/> that the file was already written, but it does not attempt to
    ///     restore the file to its previous text. A human who sees this failure and retries the resume will find
    ///     <see cref="StandingInstructionsWriter.ApplyAsync"/> refuses the second time with
    ///     <see cref="ResumeRefusal.InstructionsChangedSinceStart"/> — the pinned text and the now-written file no
    ///     longer match — which is the correct, safe outcome even though it reads as a second, different failure.
    ///     </para>
    /// </summary>
    public async ValueTask<UnitResult<ResumeFailure>> ResumeAsync(
        Guid runId, string signal, string? payload, bool applyStandingInstructions, RunPrincipal resumedBy, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signal);
        ArgumentNullException.ThrowIfNull(resumedBy);

        if (applyStandingInstructions)
        {
            if (_writer is null)
            {
                throw new InvalidOperationException(
                    "WorkflowRunGateway was constructed without a StandingInstructionsWriter; it cannot apply standing instructions.");
            }

            var run = await _store.FindAsync(runId, ct).ConfigureAwait(false);
            if (run is null)
            {
                return UnitResult<ResumeFailure>.Failure(
                    new ResumeFailure(ResumeRefusal.EngineRefused, $"Workflow run '{runId}' was not found."));
            }

            var awaiting = CheckAwaiting(run, signal, runId);
            if (awaiting.IsFailure)
            {
                return UnitResult<ResumeFailure>.Failure(new ResumeFailure(ResumeRefusal.EngineRefused, awaiting.Error));
            }

            var applied = await _writer.ApplyAsync(run, ct).ConfigureAwait(false);
            if (applied.IsFailure)
            {
                return applied;
            }
        }

        try
        {
            var result = await ResumeAsync(runId, signal, payload, resumedBy, ct).ConfigureAwait(false);
            return result.IsSuccess
                ? UnitResult<ResumeFailure>.Success()
                : UnitResult<ResumeFailure>.Failure(new ResumeFailure(ResumeRefusal.EngineRefused, result.Error));
        }
        catch (WorkflowConcurrencyException ex)
        {
            var detail = applyStandingInstructions
                ? $"The standing-instructions file was already written, but the resume itself lost a concurrency race: {ex.Message}"
                : ex.Message;
            return UnitResult<ResumeFailure>.Failure(new ResumeFailure(ResumeRefusal.EngineRefused, detail));
        }
    }

    /// <summary>
    ///     <paramref name="run"/> is parked awaiting exactly <paramref name="signal"/>, or a failure naming what
    ///     it is actually awaiting instead — the one check shared by both <c>ResumeAsync</c> overloads, so the
    ///     wording can never drift between the pre-check the public overload runs before writing and the
    ///     check the internal overload runs before resuming.
    /// </summary>
    private static Result CheckAwaiting(WorkflowRun run, string signal, Guid runId)
    {
        if (run.Status != WorkflowStatus.Awaiting)
        {
            return Result.Failure($"Workflow run '{runId}' is not awaiting any signal (status: {run.Status}).");
        }

        if (!string.Equals(run.AwaitingSignal, signal, StringComparison.Ordinal))
        {
            return Result.Failure($"Workflow run '{runId}' is awaiting signal '{run.AwaitingSignal}', not '{signal}'.");
        }

        return Result.Success();
    }

    /// <summary>Cancels <paramref name="runId"/> for <paramref name="reason"/>. A no-op past a terminal status.</summary>
    public ValueTask CancelAsync(Guid runId, string reason, CancellationToken ct) => _store.CancelAsync(runId, reason, ct);

    /// <summary>
    ///     Retries <paramref name="runId"/> at the host-action node it failed at, as <paramref name="retriedBy"/>.
    ///     Reads the run first, so the store's seq check is against the run this caller saw, and a second retry
    ///     of the same run is refused rather than re-running the action. Every refusal is the store's own, except
    ///     a run that does not exist.
    /// </summary>
    /// <remarks>
    ///     A run whose workspace the sweeper already removed is still retried. Its action then finds no workspace
    ///     and fails the run again, which costs no tokens. The sweeper keeps the workspace of every Failed run that
    ///     was approved at a gate, and that is the case retry exists for.
    /// </remarks>
    public async ValueTask<Result> RetryAsync(Guid runId, RunPrincipal retriedBy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(retriedBy);

        var run = await _store.FindAsync(runId, ct).ConfigureAwait(false);
        if (run is null)
        {
            return Result.Failure($"Workflow run '{runId}' was not found.");
        }

        return await _store.RetryFailedNodeAsync(
            runId, new WorkflowRetryRequest { ExpectedSeq = run.CurrentSeq, RetriedBy = retriedBy }, ct).ConfigureAwait(false);
    }
}
