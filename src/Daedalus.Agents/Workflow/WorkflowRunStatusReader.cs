using Daedalus.Application.Abstractions;
using Thalos.Workflow;

namespace Daedalus.Agents.Workflow;

/// <summary>The <see cref="IWorkflowRunStatusReader"/> of a workflow-enabled host, over the undecorated <see cref="IWorkflowStore"/>.</summary>
internal sealed class WorkflowRunStatusReader(IWorkflowStore store) : IWorkflowRunStatusReader
{
    /// <inheritdoc />
    public async ValueTask<WorkflowRunStatus> ReadAsync(Guid runId, CancellationToken ct)
    {
        var run = await store.FindAsync(runId, ct).ConfigureAwait(false);
        if (run is null)
        {
            return WorkflowRunStatus.Unknown;
        }

        var state = run.Status switch
        {
            WorkflowStatus.Running => WorkflowRunState.Running,
            WorkflowStatus.Awaiting => WorkflowRunState.Awaiting,
            WorkflowStatus.Succeeded => WorkflowRunState.Succeeded,
            WorkflowStatus.Failed => WorkflowRunState.Failed,
            WorkflowStatus.Cancelled => WorkflowRunState.Cancelled,
            _ => WorkflowRunState.Unknown,
        };
        return new WorkflowRunStatus(state, PullRequestLink.Read(run).Link);
    }
}
