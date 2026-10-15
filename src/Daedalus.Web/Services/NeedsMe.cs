using Daedalus.Application.DTOs.WorkflowRuns;
using Daedalus.Web.Auth;
using Daedalus.Web.State.Runs;

namespace Daedalus.Web.Services;

/// <summary>
///     What Home's four cards read from the run list, and what each keeps. Every card reads one bounded page, newest
///     first: the list reads each row's run once for its pull request link (#367), so Home never pages further.
/// </summary>
public static class NeedsMe
{
    /// <summary>The page each card reads.</summary>
    public const int Take = 20;

    /// <summary>How many pull requests the pull requests card lists.</summary>
    public const int PullRequestCount = 5;

    /// <summary>How far back the failed card looks.</summary>
    public static readonly TimeSpan FailedWindow = TimeSpan.FromDays(7);

    /// <summary>The awaiting card's read: parked runs, of which it keeps those at the human gate.</summary>
    public static RunListQuery AwaitingQuery { get; } = new("Awaiting", null, null, Take);

    /// <summary>The running card's read.</summary>
    public static RunListQuery RunningQuery { get; } = new("Running", null, null, Take);

    /// <summary>The failed card's read, of which it keeps the last <see cref="FailedWindow"/>.</summary>
    public static RunListQuery FailedQuery { get; } = new("Failed", null, null, Take);

    /// <summary>The pull requests card's read: every status, since a run that opened one may since have failed or still run.</summary>
    public static RunListQuery PullRequestQuery { get; } = new(null, null, null, Take);

    /// <summary>The runs parked at the human gate; a run waiting on another signal is not an approval.</summary>
    public static IReadOnlyList<WorkflowRunListItem> AwaitingApproval(IReadOnlyList<WorkflowRunListItem> runs) =>
        [.. runs.Where(r => string.Equals(r.AwaitingSignal, RunSignals.HumanApproval, StringComparison.Ordinal))];

    /// <summary>
    ///     The runs that last changed within <see cref="FailedWindow"/> of <paramref name="now"/>. A failed run's last change
    ///     is its failure, so a run started long ago that failed today is listed.
    /// </summary>
    public static IReadOnlyList<WorkflowRunListItem> FailedSince(IReadOnlyList<WorkflowRunListItem> runs, DateTimeOffset now) =>
        [.. runs.Where(r => r.UpdatedAt >= now - FailedWindow)];

    /// <summary>The first <see cref="PullRequestCount"/> runs, in the list's newest-first order, that have a pull request.</summary>
    public static IReadOnlyList<WorkflowRunListItem> RecentPullRequests(IReadOnlyList<WorkflowRunListItem> runs) =>
        [.. runs.Where(r => r.PrUrl is not null).Take(PullRequestCount)];

    /// <summary>A row's name: the run's task, or, for a run with no task, its process and short id.</summary>
    public static string Name(WorkflowRunListItem run) => run.TaskTitle ?? $"{run.Process} run {run.Id.ToString()[..8]}";

    /// <summary>
    ///     The reason a card shows for a failed read, or null for a request that failed because its session ended: sign-in
    ///     is under way and the layout already says so.
    /// </summary>
    public static string? ShownError(string? error) => SessionExpiry.IsSigningIn(error) ? null : error;
}
