using Thalos.Workflow;
using ZeroAlloc.Results;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     An <see cref="IWorkflowStore"/> that answers one run and records which member was called, for tests of the
///     store decorators this host puts in front of the dispatcher.
/// </summary>
internal sealed class RecordingWorkflowStore(WorkflowRun run) : IWorkflowStore
{
    public WorkflowRun? Run { get; } = run;

    public NodeResult? Completed { get; private set; }

    public string? FailureMessage { get; private set; }

    public ValueTask<WorkflowRun?> FindAsync(Guid runId, CancellationToken ct) => new(Run);

    public ValueTask CompleteNodeAsync(Guid runId, long seq, WorkflowTransition transition, NodeResult result, CancellationToken ct)
    {
        Completed = result;
        return ValueTask.CompletedTask;
    }

    public ValueTask FailAsync(Guid runId, string errorMessage, CancellationToken ct)
    {
        FailureMessage = errorMessage;
        return ValueTask.CompletedTask;
    }

    public ValueTask<Guid> StartAsync(string process, int version, string correlationKey, string startNode, IReadOnlyDictionary<string, object?>? initialVariables, CancellationToken ct) =>
        throw new NotSupportedException();

    public ValueTask<Guid> StartAsync(WorkflowStartRequest request, CancellationToken ct) => throw new NotSupportedException();

    public ValueTask<Result> ResumeAsync(Guid runId, string signal, string? payload, CancellationToken ct) => throw new NotSupportedException();

    public ValueTask<bool> FailStrandedAsync(Guid runId, long expectedSeq, string errorMessage, CancellationToken ct) => throw new NotSupportedException();

    public ValueTask CancelAsync(Guid runId, string reason, CancellationToken ct) => throw new NotSupportedException();

    public ValueTask<IReadOnlyList<WorkflowRun>> FindStrandedAsync(TimeSpan olderThan, CancellationToken ct) => throw new NotSupportedException();
}
