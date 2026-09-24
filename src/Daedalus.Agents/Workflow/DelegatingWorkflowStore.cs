using Thalos.Workflow;
using ZeroAlloc.Results;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     Forwards every <see cref="IWorkflowStore"/> member to an inner store. Exists so the two decorators this
///     host puts between <see cref="WorkflowNodeDispatcher"/> and <c>OrmWorkflowStore</c> —
///     <see cref="WorkflowRunModeStore"/> on the write side and <see cref="ReviewHandoffWorkflowStore"/> on the
///     read side — each override the one member they are about instead of restating nine pass-throughs apiece.
/// </summary>
/// <remarks>
///     Every member is <see langword="virtual"/>, and none of them is overridden here: this type adds no
///     behaviour of its own and changes none. That matters for the concurrency guarantees
///     <c>OrmWorkflowStore</c> documents — the xmin check, the seq check, the one-transaction rule — which are
///     all inside the inner store and are neither weakened nor duplicated by anything on this path.
/// </remarks>
internal abstract class DelegatingWorkflowStore(IWorkflowStore inner) : IWorkflowStore
{
    /// <summary>The store every member forwards to.</summary>
    protected IWorkflowStore Inner { get; } = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <inheritdoc />
    public virtual ValueTask<Guid> StartAsync(WorkflowStartRequest request, CancellationToken ct) => Inner.StartAsync(request, ct);

    /// <inheritdoc />
    public virtual ValueTask<WorkflowRun?> FindAsync(Guid runId, CancellationToken ct) => Inner.FindAsync(runId, ct);

    /// <inheritdoc />
    public virtual ValueTask CompleteNodeAsync(Guid runId, long seq, WorkflowTransition transition, NodeResult result, CancellationToken ct) =>
        Inner.CompleteNodeAsync(runId, seq, transition, result, ct);

    /// <inheritdoc />
    public virtual ValueTask<Result> ResumeAsync(Guid runId, string signal, string? payload, CancellationToken ct) =>
        Inner.ResumeAsync(runId, signal, payload, ct);

    /// <inheritdoc />
    public virtual ValueTask FailAsync(Guid runId, string errorMessage, CancellationToken ct) => Inner.FailAsync(runId, errorMessage, ct);

    /// <inheritdoc />
    public virtual ValueTask<bool> FailStrandedAsync(Guid runId, long expectedSeq, string errorMessage, CancellationToken ct) =>
        Inner.FailStrandedAsync(runId, expectedSeq, errorMessage, ct);

    /// <inheritdoc />
    public virtual ValueTask CancelAsync(Guid runId, string reason, CancellationToken ct) => Inner.CancelAsync(runId, reason, ct);

    /// <inheritdoc />
    public virtual ValueTask<IReadOnlyList<WorkflowRun>> FindStrandedAsync(TimeSpan olderThan, CancellationToken ct) =>
        Inner.FindStrandedAsync(olderThan, ct);
}
