using ZeroAlloc.Results;

namespace Daedalus.Infrastructure.Services.GitHub;

/// <summary>Reads repository activity from GitHub for the digest.</summary>
public interface IGitHubReader
{
    Task<RepoActivity> GetActivityAsync(RepoRef repo, DateTime sinceUtc, CancellationToken ct = default);

    Task<Result<string>> GetDefaultBranchAsync(RepoRef repo, CancellationToken ct = default);

    /// <summary>One issue, or <see langword="null"/> when GitHub answers 404. Other failures are failed results.</summary>
    Task<Result<IssueDetail?>> GetIssueAsync(RepoRef repo, int number, CancellationToken ct = default);

    /// <summary>
    ///     Issues in <paramref name="repo"/> matching <paramref name="query"/>, at most <paramref name="limit"/>, capped at
    ///     <see cref="GitHubApi.MaxSearchHits"/>. A query naming its own <c>repo:</c>, <c>org:</c>, <c>user:</c> or
    ///     <c>owner:</c> qualifier is refused, and a hit from another repository or a pull request is dropped.
    /// </summary>
    Task<Result<IReadOnlyList<IssueHit>>> SearchIssuesAsync(RepoRef repo, string query, IssueSearchState state, int limit, CancellationToken ct = default);

    /// <summary>Every issue and pull request in <paramref name="repo"/> updated at or after <paramref name="since"/>.</summary>
    Task<Result<IReadOnlyList<IssueText>>> ListIssuesUpdatedSinceAsync(RepoRef repo, DateTimeOffset since, CancellationToken ct = default);

    /// <summary>Every comment on issue or pull request <paramref name="number"/> updated at or after <paramref name="since"/>.</summary>
    Task<Result<IReadOnlyList<IssueCommentText>>> ListIssueCommentsSinceAsync(RepoRef repo, int number, DateTimeOffset since, CancellationToken ct = default);

    /// <summary>The commit pull request <paramref name="number"/>'s head points at.</summary>
    Task<Result<string>> GetPullRequestHeadShaAsync(RepoRef repo, int number, CancellationToken ct = default);
}
