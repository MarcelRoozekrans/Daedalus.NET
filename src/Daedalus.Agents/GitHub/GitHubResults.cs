namespace Daedalus.Agents.GitHub;

/// <summary>
///     One category of repository activity, and whether we actually managed to read it.
/// </summary>
/// <remarks>
///     <see cref="Unavailable"/> exists so that "nothing happened" and "we could not look" are different values
///     rather than the same empty list. Collapsing them produces a digest that calls a repository quiet when it does
///     not know that — the same distinction phase 1.6 drew between <c>Delivered</c> and <c>DeliveryUnknown</c>.
/// </remarks>
public sealed record CategoryResult<T>(IReadOnlyList<T> Items, bool Truncated, string? Unavailable)
{
    /// <summary>True when the category was read successfully, whatever it contained.</summary>
    public bool Checked => Unavailable is null;

#pragma warning disable CA1000
    public static CategoryResult<T> Ok(IReadOnlyList<T> items, bool truncated) => new(items, truncated, null);

    public static CategoryResult<T> Failed(string reason) => new([], false, reason);
#pragma warning restore CA1000
}

public sealed record CommitSummary(string Sha, string Message, string Author, DateTime CommittedAtUtc);

public sealed record PullRequestSummary(int Number, string Title, string Author, DateTime UpdatedAtUtc, bool Merged);

public sealed record IssueSummary(int Number, string Title, string State, DateTime UpdatedAtUtc);

public sealed record WorkflowRunSummary(long Id, string Name, string Conclusion, string HeadBranch, DateTime RunStartedAtUtc);

/// <summary>Everything one sweep of a repository found, with the window it actually covered.</summary>
public sealed record RepoActivity(
    RepoRef Repo,
    DateTime WindowStartUtc,
    DateTime WindowEndUtc,
    CategoryResult<CommitSummary> Commits,
    CategoryResult<PullRequestSummary> MergedPullRequests,
    CategoryResult<PullRequestSummary> OpenPullRequests,
    CategoryResult<IssueSummary> Issues,
    CategoryResult<WorkflowRunSummary> FailedRuns);
