using Radzen;

namespace Daedalus.Web.Services;

/// <summary>
///     Labels for a task's status, shared by every page that shows one. 5 and 6 are derived from the task's manufacture run
///     and never stored (phase 2.8).
/// </summary>
public static class TaskStatusLabels
{
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

    public static BadgeStyle Style(int status) => status switch
    {
        0 => BadgeStyle.Warning,
        1 => BadgeStyle.Info,
        2 => BadgeStyle.Success,
        3 => BadgeStyle.Danger,
        5 => BadgeStyle.Primary,
        _ => BadgeStyle.Light,
    };

    /// <summary>Whether a Manufacture button is offered: not while a run is live, and not once the task completed.</summary>
    public static bool IsStartable(int status) => status is 0 or 3 or 4 or 6;

    /// <summary>
    ///     Whether the task carries the retired loop's settings: only a task from before phase 2.8 has
    ///     <c>MaxIterations</c> above zero. Those fields are shown read-only, for such a task only.
    /// </summary>
    public static bool HasLoopHistory(TaskDto task) => task.MaxIterations > 0;

    /// <summary>The pull request link to render for a task, or null when it has none.</summary>
    public static string? PullRequestHref(TaskDto task) => task.PullRequestUrl?.ToString();
}
