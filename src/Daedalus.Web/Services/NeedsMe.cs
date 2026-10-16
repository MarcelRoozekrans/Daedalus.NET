using System.Globalization;
using Daedalus.Application.DTOs.WorkflowRuns;
using Daedalus.Web.State.Runs;

namespace Daedalus.Web.Services;

/// <summary>
///     What Home's four cards read from the run list. Each card reads one bounded page, newest first: the list reads each
///     row's run once for its pull request link (#367), so Home never pages further. The awaiting and failed cards let
///     the server filter, so their pages hold only their own runs and a count is exact unless the list says it has more.
/// </summary>
public static class NeedsMe
{
    /// <summary>The page each card reads.</summary>
    public const int Take = 20;

    /// <summary>How many pull requests the pull requests card lists.</summary>
    public const int PullRequestCount = 5;

    /// <summary>The manufacture process, the one that opens pull requests.</summary>
    public const string ManufactureProcess = "manufacture";

    /// <summary>How far back the failed card looks.</summary>
    public static readonly TimeSpan FailedWindow = TimeSpan.FromDays(7);

    /// <summary>The awaiting card's read: runs parked at the human gate. A run waiting on another signal is not an approval.</summary>
    public static RunListQuery AwaitingQuery { get; } = new("Awaiting", null, null, Take, Signal: RunSignals.HumanApproval);

    /// <summary>The running card's read.</summary>
    public static RunListQuery RunningQuery { get; } = new("Running", null, null, Take);

    /// <summary>
    ///     The pull requests card's read: manufacture runs of every status, since a run that opened one may since have
    ///     failed or still run. The link is a run variable, not on the run summary, so the server cannot filter on it
    ///     without reading every run, and the card keeps those with one from this page; see #367.
    /// </summary>
    public static RunListQuery PullRequestQuery { get; } = new(null, ManufactureProcess, null, Take);

    /// <summary>
    ///     The failed card's read: failed runs that last changed within <see cref="FailedWindow"/> of
    ///     <paramref name="now"/>. A failed run's last change is its failure, so a run started long ago that failed today is
    ///     listed.
    /// </summary>
    public static RunListQuery FailedQuery(DateTimeOffset now) => new("Failed", null, null, Take, UpdatedSince: now - FailedWindow);

    /// <summary>The first <see cref="PullRequestCount"/> runs, in the list's newest-first order, that have a pull request.</summary>
    public static IReadOnlyList<WorkflowRunListItem> RecentPullRequests(IReadOnlyList<WorkflowRunListItem> runs) =>
        [.. runs.Where(r => r.PrUrl is not null).Take(PullRequestCount)];

    /// <summary>
    ///     What the pull requests card says when it found none: there are none at all only when the page it read was the
    ///     whole list; otherwise only the latest <see cref="Take"/> runs had none.
    /// </summary>
    public static string NoPullRequests(bool more) => more ? InLatest("No pull requests") : "No pull requests yet.";

    /// <summary>
    ///     What the awaiting card says when it is empty. The server filters its signal by scanning, so an empty page with a
    ///     cursor means it read <see cref="RunListLimits.ScanCap"/> runs and found none waiting for approval among them.
    /// </summary>
    public static string NothingWaiting(bool more) => more ? Scanned("Nothing waiting for approval") : "Nothing waiting for approval.";

    /// <summary>
    ///     What the running card says when it is empty. The store filters on status itself, so a cursor here follows a full
    ///     page of <see cref="Take"/>; the text says how much that page held.
    /// </summary>
    public static string NothingRunning(bool more) => more ? InLatest("Nothing running") : "Nothing running.";

    /// <summary>
    ///     What the failed card says when it is empty. The server filters the 7 days by scanning, so an empty page with a
    ///     cursor means it read <see cref="RunListLimits.ScanCap"/> runs and found no recent failure among them.
    /// </summary>
    public static string NoFailedRuns(bool more) =>
        more ? Scanned("No failed runs in the last 7 days") : "No failed runs in the last 7 days.";

    /// <summary><paramref name="none"/>, about the latest <see cref="Take"/> runs: the one page the card read.</summary>
    private static string InLatest(string none) => string.Create(CultureInfo.InvariantCulture, $"{none} in the latest {Take} runs.");

    /// <summary><paramref name="none"/>, about the <see cref="RunListLimits.ScanCap"/> runs the server checked for the card.</summary>
    private static string Scanned(string none) =>
        string.Create(CultureInfo.InvariantCulture, $"{none} in the latest {RunListLimits.ScanCap} runs checked.");

    /// <summary>A row's name: the run's task, or, for a run with no task, its process and short id.</summary>
    public static string Name(WorkflowRunListItem run) => run.TaskTitle ?? $"{run.Process} run {run.Id.ToString()[..8]}";
}
