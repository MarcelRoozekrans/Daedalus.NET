using System.Text.Json;

namespace Daedalus.Infrastructure.Services.GitHub;

// GitHub's answers, as GitHubJsonContext reads them with snake_case names. Every field is nullable, so a missing field is
// an empty value, as TryGetProperty gave before phase 2.11. GitHubApi decides which fields it requires and answers
// Unreadable when one is missing (ruling P11). Timestamps stay strings, parsed by GitHubApi with today's fallbacks.

/// <summary><c>GET /repos/{owner}/{repo}</c>.</summary>
internal sealed record GitHubRepositoryDto(string? DefaultBranch);

/// <summary>One entry of <c>GET /repos/{owner}/{repo}/commits</c>.</summary>
internal sealed record GitHubCommitDto(string? Sha, GitHubCommitDetailDto? Commit);

internal sealed record GitHubCommitDetailDto(string? Message, GitHubCommitAuthorDto? Author);

internal sealed record GitHubCommitAuthorDto(string? Name, string? Date);

/// <summary>A user, as <c>GET /user</c> and every <c>user</c> field carry one.</summary>
internal sealed record GitHubUserDto(string? Login);

internal sealed record GitHubRefDto(string? Sha);

/// <summary>A pull request, from a listing, <c>GET .../pulls/{number}</c> or the create.</summary>
internal sealed record GitHubPullRequestDto(
    int? Number, string? Title, string? Url, string? HtmlUrl, GitHubUserDto? User, string? UpdatedAt, string? MergedAt, GitHubRefDto? Head);

internal sealed record GitHubLabelDto(string? Name);

/// <summary>
///     An issue, or a pull request answered by an issue endpoint. <see cref="PullRequest"/> is <see cref="JsonValueKind.Undefined"/>
///     when the field is absent, and anything else, JSON <c>null</c> included, when it is present.
/// </summary>
internal sealed record GitHubIssueDto(
    int? Number,
    string? Title,
    string? State,
    string? Body,
    string? HtmlUrl,
    string? UpdatedAt,
    IReadOnlyList<GitHubLabelDto>? Labels,
    GitHubUserDto? User,
    JsonElement PullRequest,
    string? RepositoryUrl);

internal sealed record GitHubCommentDto(long? Id, string? Body, string? HtmlUrl, GitHubUserDto? User);

/// <summary><c>GET /search/issues</c>.</summary>
internal sealed record GitHubSearchResultDto(IReadOnlyList<GitHubIssueDto>? Items);

/// <summary><c>GET /repos/{owner}/{repo}/actions/runs</c>.</summary>
internal sealed record GitHubWorkflowRunsDto(IReadOnlyList<GitHubWorkflowRunDto>? WorkflowRuns);

internal sealed record GitHubWorkflowRunDto(long? Id, string? Name, string? Conclusion, string? HeadBranch, string? RunStartedAt);

// Request bodies, written with the same snake_case policy.

internal sealed record GitHubCreatePullRequestBody(string Title, string Body, string Head, string Base, bool Draft);

internal sealed record GitHubCreateIssueBody(string Title, string Body);

internal sealed record GitHubCommentBody(string Body);

internal sealed record GitHubLabelsBody(IReadOnlyList<string> Labels);

internal sealed record GitHubIssueStateBody(string State);
