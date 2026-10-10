using Daedalus.Application.Abstractions;
using DomainTask = Daedalus.Domain.Entities.Task;

namespace Daedalus.Application.Services;

/// <summary>
///     The refusal of a change to a task whose run is live (amendment A7). <c>TasksController</c> maps a failure that
///     starts with <see cref="LiveRunPrefix"/> to 409.
/// </summary>
public static class TaskRunGuard
{
    /// <summary>The start of every live-run refusal.</summary>
    public const string LiveRunPrefix = "The task has a live manufacture run";

    /// <summary>The refusal text, naming the run and its state.</summary>
    public static string LiveRun(DomainTask task, WorkflowRunStatus run) =>
        $"{LiveRunPrefix}: run {task.WorkflowRunId} is {run.State}. Wait until it finishes, or cancel it first.";
}
