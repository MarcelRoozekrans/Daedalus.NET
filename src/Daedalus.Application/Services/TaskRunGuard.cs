using Daedalus.Application.Abstractions;
using DomainTask = Daedalus.Domain.Entities.Task;

namespace Daedalus.Application.Services;

/// <summary>
///     The refusal of a change to a task whose run is live, or of a delete of its project (amendment A7).
///     <c>TasksController</c> and <c>ProjectsController</c> map a failure that
///     starts with <see cref="LiveRunPrefix"/> or <see cref="ChangedPrefix"/> to 409. On a host whose engine is off the
///     run reads Unknown, which is not live, so update and delete go through (consistent with A2).
/// </summary>
public static class TaskRunGuard
{
    /// <summary>The start of every live-run refusal.</summary>
    public const string LiveRunPrefix = "The task has a live manufacture run";

    /// <summary>The start of every refusal of a save that lost a race to another write of the same task.</summary>
    public const string ChangedPrefix = "The task was changed by another request";

    /// <summary>Whether <paramref name="error"/> is a refusal that maps to 409.</summary>
    public static bool IsConflict(string error) =>
        error.StartsWith(LiveRunPrefix, StringComparison.Ordinal) || error.StartsWith(ChangedPrefix, StringComparison.Ordinal);

    /// <summary>The refusal text of a save that lost a race, such as one that a manufacture run attach won.</summary>
    public static string ChangedUnderneath(Guid taskId) =>
        $"{ChangedPrefix}: task {taskId} was modified after it was read. Reload it and try again.";

    /// <summary>The refusal text, naming the run and its state.</summary>
    public static string LiveRun(DomainTask task, WorkflowRunStatus run) =>
        $"{LiveRunPrefix}: run {task.WorkflowRunId} is {run.State}. Wait until it finishes, or cancel it first.";

    /// <summary>The refusal text of a project delete, naming the task whose run is live, the run and its state.</summary>
    public static string LiveRunInProject(DomainTask task, WorkflowRunStatus run) =>
        $"{LiveRunPrefix}: task {task.TaskId}'s run {task.WorkflowRunId} is {run.State}. Wait until it finishes, or cancel it, before deleting the project.";
}
