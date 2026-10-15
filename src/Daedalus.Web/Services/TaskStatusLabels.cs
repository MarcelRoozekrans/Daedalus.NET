using Radzen;

namespace Daedalus.Web.Services;

/// <summary>
///     What the Web decides from a task's status, shared by every page that shows one. A task with a run shows its run's
///     badge, through <see cref="RunStatusLabels"/>; a task with no run shows its own stored or derived status by
///     <see cref="Name"/>, muted. The derived <c>Status</c>, where 5 and 6 come from the run and are never stored (phase
///     2.8), decides what the task offers.
/// </summary>
public static class TaskStatusLabels
{
    /// <summary>
    ///     The text of a task's own status, stored or derived. A badge shows it only for a task with no run: a new task,
    ///     Pending, or frozen history from the retired loop, such as Completed or Abandoned. A task with a run shows its
    ///     run's status through <see cref="RunStatusLabels"/> instead.
    /// </summary>
    public static string Name(int status) => status switch
    {
        0 => "Pending",
        1 => "In Progress",
        2 => "Completed",
        3 => "Failed",
        4 => "Abandoned",
        5 => "Awaiting Approval",
        6 => "Cancelled",
        _ => "Unknown",
    };

    /// <summary>
    ///     The muted badge text of a task with no run, its own status's <see cref="Name"/>; null for a task with a run,
    ///     whose badge is its run's.
    /// </summary>
    public static string? NoRunText(TaskDto task) => task.RunStatus is null ? Name(task.Status) : null;

    /// <summary>The severity of a task with no run: muted, because its text carries the meaning.</summary>
    public const BadgeStyle NoRunStyle = BadgeStyle.Light;

    /// <summary>The badge text of a task: its run's, as <see cref="RunStatusLabels"/> names it, or its own when it has none.</summary>
    public static string BadgeText(TaskDto task) => NoRunText(task) ?? RunStatusLabels.Text(task.RunStatus!, task.AwaitingSignal);

    /// <summary>The badge severity of a task: its run's, as <see cref="RunStatusLabels"/> maps it, or <see cref="NoRunStyle"/>.</summary>
    public static BadgeStyle BadgeStyleOf(TaskDto task) =>
        task.RunStatus is { } run ? RunStatusLabels.Style(run, task.AwaitingSignal) : NoRunStyle;

    /// <summary>
    ///     Whether the task can be edited: not while a run is live, and not once the task completed. A task whose run
    ///     failed or was cancelled stays editable. The server's <c>TaskStatusDerivation.IsEditable</c> applies the same set.
    /// </summary>
    public static bool IsEditable(int status) => status is 0 or 3 or 4 or 6;

    /// <summary>
    ///     Whether a Manufacture button is offered: for the statuses <see cref="IsEditable"/> allows, and for an orphaned
    ///     claim of the retired loop, a task stored In Progress with no run, which the server starts.
    /// </summary>
    public static bool IsStartable(TaskDto task) =>
        IsEditable(task.Status) || (task.Status == 1 && task.WorkflowRunId is null);

    /// <summary>
    ///     Whether the task carries the retired loop's settings: only a task from before phase 2.8 has
    ///     <c>MaxIterations</c> above zero. Those fields are shown read-only, for such a task only.
    /// </summary>
    public static bool HasLoopHistory(TaskDto task) => task.MaxIterations > 0;

    /// <summary>The pull request link to render for a task, or null when it has none.</summary>
    public static string? PullRequestHref(TaskDto task) => task.PullRequestUrl?.ToString();

    /// <summary>
    ///     The text of a task's pull request link: <c>#N</c> for a URL that ends in the pull request's number, else
    ///     "Pull request"; null when the task has none.
    /// </summary>
    public static string? PullRequestText(TaskDto task)
    {
        if (task.PullRequestUrl is not { } url)
        {
            return null;
        }

        var last = url.AbsolutePath.TrimEnd('/').Split('/')[^1];
        return last.Length > 0 && last.All(char.IsAsciiDigit) ? $"#{last}" : "Pull request";
    }
}
