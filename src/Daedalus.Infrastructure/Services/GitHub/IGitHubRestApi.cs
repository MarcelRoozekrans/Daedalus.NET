using ZeroAlloc.Rest.Attributes;
using ZeroAlloc.Results;

namespace Daedalus.Infrastructure.Services.GitHub;

/// <summary>The headers every GitHub request carries whatever the caller; the token and User-Agent come per call.</summary>
internal static class GitHubRestHeaders
{
    public const string Accept = "application/vnd.github+json";

    public const string ApiVersion = "2022-11-28";
}

/// <summary>
///     GitHub's REST surface as Daedalus uses it, one method per endpoint. ZeroAlloc.Rest generates
///     <c>GitHubRestApiClient</c>; only <see cref="GitHubApi"/> uses it. Routes are relative and have no leading slash, so
///     the <see cref="System.Net.Http.HttpClient.BaseAddress"/> path of a GitHub Enterprise server is kept. An error body
///     is read up to 2 KiB, for <see cref="GitHubError.GitHubSaid"/> and to recognise a secondary rate limit only; it never
///     reaches <see cref="GitHubError.Message"/>.
/// </summary>
[ZeroAllocRestClient(MaxErrorBodyBytes = 2048)]
[ErrorMapper(typeof(GitHubErrorMapper))]
internal interface IGitHubRestApi
{
    [Get("repos/{owner}/{repo}")]
    [Header("Accept", Value = GitHubRestHeaders.Accept)]
    [Header("X-GitHub-Api-Version", Value = GitHubRestHeaders.ApiVersion)]
    Task<Result<GitHubRepositoryDto, GitHubError>> GetRepositoryAsync(
        string owner, string repo,
        [Header("Authorization")] string authorization, [Header("User-Agent")] string userAgent, CancellationToken ct);

    [Get("repos/{owner}/{repo}/commits")]
    [Header("Accept", Value = GitHubRestHeaders.Accept)]
    [Header("X-GitHub-Api-Version", Value = GitHubRestHeaders.ApiVersion)]
    Task<Result<IReadOnlyList<GitHubCommitDto>, GitHubError>> ListCommitsAsync(
        string owner, string repo, [Query] string sha, [Query] string since, [Query(Name = "per_page")] int perPage,
        [Header("Authorization")] string authorization, [Header("User-Agent")] string userAgent, CancellationToken ct);

    /// <summary>One listing for three queries: the digest's merged and open reads and the open-by-head lookup. A null value is left out.</summary>
    [Get("repos/{owner}/{repo}/pulls")]
    [Header("Accept", Value = GitHubRestHeaders.Accept)]
    [Header("X-GitHub-Api-Version", Value = GitHubRestHeaders.ApiVersion)]
    Task<Result<IReadOnlyList<GitHubPullRequestDto>, GitHubError>> ListPullRequestsAsync(
        string owner, string repo, [Query] string state, [Query] string? head, [Query] string? sort, [Query] string? direction,
        [Query(Name = "per_page")] int? perPage,
        [Header("Authorization")] string authorization, [Header("User-Agent")] string userAgent, CancellationToken ct);

    [Get("repos/{owner}/{repo}/pulls/{number}")]
    [Header("Accept", Value = GitHubRestHeaders.Accept)]
    [Header("X-GitHub-Api-Version", Value = GitHubRestHeaders.ApiVersion)]
    Task<Result<GitHubPullRequestDto, GitHubError>> GetPullRequestAsync(
        string owner, string repo, int number,
        [Header("Authorization")] string authorization, [Header("User-Agent")] string userAgent, CancellationToken ct);

    [Post("repos/{owner}/{repo}/pulls")]
    [Header("Accept", Value = GitHubRestHeaders.Accept)]
    [Header("X-GitHub-Api-Version", Value = GitHubRestHeaders.ApiVersion)]
    Task<Result<GitHubPullRequestDto, GitHubError>> CreatePullRequestAsync(
        string owner, string repo, [Body] GitHubCreatePullRequestBody body,
        [Header("Authorization")] string authorization, [Header("User-Agent")] string userAgent, CancellationToken ct);

    [Get("repos/{owner}/{repo}/issues/{number}")]
    [Header("Accept", Value = GitHubRestHeaders.Accept)]
    [Header("X-GitHub-Api-Version", Value = GitHubRestHeaders.ApiVersion)]
    Task<Result<GitHubIssueDto, GitHubError>> GetIssueAsync(
        string owner, string repo, int number,
        [Header("Authorization")] string authorization, [Header("User-Agent")] string userAgent, CancellationToken ct);

    /// <summary>The digest's issue read leaves <paramref name="page"/> null; the marker scan pages through.</summary>
    [Get("repos/{owner}/{repo}/issues")]
    [Header("Accept", Value = GitHubRestHeaders.Accept)]
    [Header("X-GitHub-Api-Version", Value = GitHubRestHeaders.ApiVersion)]
    Task<Result<IReadOnlyList<GitHubIssueDto>, GitHubError>> ListIssuesAsync(
        string owner, string repo, [Query] string since, [Query] string state, [Query(Name = "per_page")] int perPage, [Query] int? page,
        [Header("Authorization")] string authorization, [Header("User-Agent")] string userAgent, CancellationToken ct);

    [Post("repos/{owner}/{repo}/issues")]
    [Header("Accept", Value = GitHubRestHeaders.Accept)]
    [Header("X-GitHub-Api-Version", Value = GitHubRestHeaders.ApiVersion)]
    Task<Result<GitHubIssueDto, GitHubError>> CreateIssueAsync(
        string owner, string repo, [Body] GitHubCreateIssueBody body,
        [Header("Authorization")] string authorization, [Header("User-Agent")] string userAgent, CancellationToken ct);

    [Post("repos/{owner}/{repo}/issues/{number}/comments")]
    [Header("Accept", Value = GitHubRestHeaders.Accept)]
    [Header("X-GitHub-Api-Version", Value = GitHubRestHeaders.ApiVersion)]
    Task<UnitResult<GitHubError>> CreateCommentAsync(
        string owner, string repo, int number, [Body] GitHubCommentBody body,
        [Header("Authorization")] string authorization, [Header("User-Agent")] string userAgent, CancellationToken ct);

    [Get("repos/{owner}/{repo}/issues/{number}/comments")]
    [Header("Accept", Value = GitHubRestHeaders.Accept)]
    [Header("X-GitHub-Api-Version", Value = GitHubRestHeaders.ApiVersion)]
    Task<Result<IReadOnlyList<GitHubCommentDto>, GitHubError>> ListCommentsAsync(
        string owner, string repo, int number, [Query] string since, [Query(Name = "per_page")] int perPage, [Query] int page,
        [Header("Authorization")] string authorization, [Header("User-Agent")] string userAgent, CancellationToken ct);

    [Post("repos/{owner}/{repo}/issues/{number}/labels")]
    [Header("Accept", Value = GitHubRestHeaders.Accept)]
    [Header("X-GitHub-Api-Version", Value = GitHubRestHeaders.ApiVersion)]
    Task<UnitResult<GitHubError>> AddLabelsAsync(
        string owner, string repo, int number, [Body] GitHubLabelsBody body,
        [Header("Authorization")] string authorization, [Header("User-Agent")] string userAgent, CancellationToken ct);

    [Patch("repos/{owner}/{repo}/issues/{number}")]
    [Header("Accept", Value = GitHubRestHeaders.Accept)]
    [Header("X-GitHub-Api-Version", Value = GitHubRestHeaders.ApiVersion)]
    Task<UnitResult<GitHubError>> UpdateIssueStateAsync(
        string owner, string repo, int number, [Body] GitHubIssueStateBody body,
        [Header("Authorization")] string authorization, [Header("User-Agent")] string userAgent, CancellationToken ct);

    [Get("search/issues")]
    [Header("Accept", Value = GitHubRestHeaders.Accept)]
    [Header("X-GitHub-Api-Version", Value = GitHubRestHeaders.ApiVersion)]
    Task<Result<GitHubSearchResultDto, GitHubError>> SearchIssuesAsync(
        [Query] string q, [Query(Name = "per_page")] int perPage,
        [Header("Authorization")] string authorization, [Header("User-Agent")] string userAgent, CancellationToken ct);

    [Get("repos/{owner}/{repo}/actions/runs")]
    [Header("Accept", Value = GitHubRestHeaders.Accept)]
    [Header("X-GitHub-Api-Version", Value = GitHubRestHeaders.ApiVersion)]
    Task<Result<GitHubWorkflowRunsDto, GitHubError>> ListWorkflowRunsAsync(
        string owner, string repo, [Query] string status, [Query(Name = "per_page")] int perPage,
        [Header("Authorization")] string authorization, [Header("User-Agent")] string userAgent, CancellationToken ct);

    [Get("user")]
    [Header("Accept", Value = GitHubRestHeaders.Accept)]
    [Header("X-GitHub-Api-Version", Value = GitHubRestHeaders.ApiVersion)]
    Task<Result<GitHubUserDto, GitHubError>> GetAuthenticatedUserAsync(
        [Header("Authorization")] string authorization, [Header("User-Agent")] string userAgent, CancellationToken ct);
}
