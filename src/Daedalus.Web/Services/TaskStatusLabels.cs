namespace Daedalus.Web.Services;

/// <summary>
///     What the Web decides from a task's status, shared by every page that shows one. A task's badge is its run's, through
///     <see cref="RunStatusLabels"/>; the derived <c>Status</c>, where 5 and 6 come from the run and are never stored
///     (phase 2.8), decides what the task offers.
/// </summary>
public static class TaskStatusLabels
{
    /// <summary>
    ///     The badge status of a task with no run: it has not been started. It is no run status, so
    ///     <see cref="RunStatusLabels"/> shows it as itself, muted.
    /// </summary>
    public const string NotStarted = "Pending";

    /// <summary>
    ///     The status a task's badge shows: its run's, mapped by <see cref="RunStatusLabels"/> exactly as the Runs page
    ///     maps it, or <see cref="NotStarted"/> for a task with no run.
    /// </summary>
    public static string BadgeStatus(TaskDto task) => task.RunStatus ?? NotStarted;

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
