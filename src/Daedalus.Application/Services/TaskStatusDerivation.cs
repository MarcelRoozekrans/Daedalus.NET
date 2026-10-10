using Daedalus.Application.Abstractions;
using DomainTask = Daedalus.Domain.Entities.Task;
using TaskStatus = Daedalus.Domain.Entities.TaskStatus;

namespace Daedalus.Application.Services;

/// <summary>A task's status as it is read: its run's status, or its stored value when it has no readable run (spec §2).</summary>
public static class TaskStatusDerivation
{
    /// <summary>
    ///     Maps <paramref name="run"/> to a task status. <see cref="WorkflowRunState.Unknown"/> means the workflow engine
    ///     is off on this host or the run does not exist; in both cases the task shows its stored status, so a task
    ///     whose run vanished, or one read on a host with the engine off, falls back to the stored value.
    /// </summary>
    public static TaskStatus Derive(DomainTask task, WorkflowRunStatus run) => run.State switch
    {
        WorkflowRunState.Running => TaskStatus.InProgress,
        WorkflowRunState.Awaiting => TaskStatus.AwaitingApproval,
        WorkflowRunState.Succeeded => TaskStatus.Completed,
        WorkflowRunState.Failed => TaskStatus.Failed,
        WorkflowRunState.Cancelled => TaskStatus.Cancelled,
        _ => task.Status,
    };

    /// <summary>
    ///     Whether a task whose derived status is <paramref name="derived"/> can be edited: it has no live run and is not
    ///     completed. A task whose run failed or was cancelled stays editable (amendment A7). The Web's
    ///     <c>TaskStatusLabels.IsEditable</c> applies the same set.
    /// </summary>
    public static bool IsEditable(TaskStatus derived) =>
        derived is TaskStatus.Pending or TaskStatus.Failed or TaskStatus.Abandoned or TaskStatus.Cancelled;

    /// <summary>The status of <paramref name="task"/>'s current run, without a lookup when it has none.</summary>
    public static async ValueTask<WorkflowRunStatus> ReadRunAsync(this IWorkflowRunStatusReader reader, DomainTask task, CancellationToken ct) =>
        task.WorkflowRunId is { } runId ? await reader.ReadAsync(runId, ct).ConfigureAwait(false) : WorkflowRunStatus.Unknown;
}
