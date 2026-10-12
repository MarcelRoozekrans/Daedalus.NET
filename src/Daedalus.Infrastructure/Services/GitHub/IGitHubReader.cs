using ZeroAlloc.Results;

namespace Daedalus.Infrastructure.Services.GitHub;

/// <summary>
///     Reads repository activity and issues from GitHub. A failure is a <see cref="GitHubError"/>, so a caller can tell "rate-limited,
///     retry later" from "forbidden" without reading text.
/// </summary>
public interface IGitHubReader
{
    /// <summary>The digest's sweep. Never a failed result: a category that could not be read says so, with the reason.</summary>
    Task<RepoActivity> GetActivityAsync(RepoRef repo, DateTime sinceUtc, CancellationToken ct = default);

    Task<Result<string, GitHubError>> GetDefaultBranchAsync(RepoRef repo, CancellationToken ct = default);

    /// <summary>One issue, or <see langword="null"/> when GitHub answers 404 or 410. Other failures are failed results.</summary>
    Task<Result<IssueDetail?, GitHubError>> GetIssueAsync(RepoRef repo, int number, CancellationToken ct = default);

    /// <summary>
    ///     Issues in <paramref name="repo"/> matching <paramref name="query"/>, at most <paramref name="limit"/>, capped at
    ///     <see cref="GitHubApi.MaxSearchHits"/>. A query naming its own <c>repo:</c>, <c>org:</c>, <c>user:</c> or
    ///     <c>owner:</c> qualifier is refused, and a hit from another repository or a pull request is dropped.
    /// </summary>
    Task<Result<IReadOnlyList<IssueHit>, GitHubError>> SearchIssuesAsync(RepoRef repo, string query, IssueSearchState state, int limit, CancellationToken ct = default);

    /// <summary>Every issue and pull request in <paramref name="repo"/> updated at or after <paramref name="since"/>.</summary>
    Task<Result<IReadOnlyList<IssueText>, GitHubError>> ListIssuesUpdatedSinceAsync(RepoRef repo, DateTimeOffset since, CancellationToken ct = default);

    /// <summary>Every comment on issue or pull request <paramref name="number"/> updated at or after <paramref name="since"/>.</summary>
    Task<Result<IReadOnlyList<IssueCommentText>, GitHubError>> ListIssueCommentsSinceAsync(RepoRef repo, int number, DateTimeOffset since, CancellationToken ct = default);

    /// <summary>The commit pull request <paramref name="number"/>'s head points at.</summary>
    Task<Result<string, GitHubError>> GetPullRequestHeadShaAsync(RepoRef repo, int number, CancellationToken ct = default);

    /// <summary>The login of the account the configured token authenticates as, which is the author of everything Daedalus writes.</summary>
    Task<Result<string, GitHubError>> GetAuthenticatedLoginAsync(CancellationToken ct = default);
}
