using Daedalus.Application.Abstractions;

namespace Daedalus.Application.Services;

/// <summary>
///     The <see cref="IWorkflowRunStatusReader"/> of a host whose workflow engine is off. It reads nothing and answers
///     <see cref="WorkflowRunStatus.Unknown"/>, so a task shows its stored status.
/// </summary>
public sealed class DisabledWorkflowRunStatusReader : IWorkflowRunStatusReader
{
    /// <inheritdoc />
    public ValueTask<WorkflowRunStatus> ReadAsync(Guid runId, CancellationToken ct) => new(WorkflowRunStatus.Unknown);
}
