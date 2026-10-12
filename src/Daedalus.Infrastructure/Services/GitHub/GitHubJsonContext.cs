using System.Text.Json.Serialization;

namespace Daedalus.Infrastructure.Services.GitHub;

/// <summary>
///     The source-generated JSON metadata for every GitHub answer and request body, so the client uses no reflection and
///     needs no IL2026 suppression. GitHub's field names are snake_case.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(GitHubRepositoryDto))]
[JsonSerializable(typeof(IReadOnlyList<GitHubCommitDto>))]
[JsonSerializable(typeof(GitHubPullRequestDto))]
[JsonSerializable(typeof(IReadOnlyList<GitHubPullRequestDto>))]
[JsonSerializable(typeof(GitHubIssueDto))]
[JsonSerializable(typeof(IReadOnlyList<GitHubIssueDto>))]
[JsonSerializable(typeof(IReadOnlyList<GitHubCommentDto>))]
[JsonSerializable(typeof(GitHubSearchResultDto))]
[JsonSerializable(typeof(GitHubWorkflowRunsDto))]
[JsonSerializable(typeof(GitHubUserDto))]
[JsonSerializable(typeof(GitHubCreatePullRequestBody))]
[JsonSerializable(typeof(GitHubCreateIssueBody))]
[JsonSerializable(typeof(GitHubCommentBody))]
[JsonSerializable(typeof(GitHubLabelsBody))]
[JsonSerializable(typeof(GitHubIssueStateBody))]
internal sealed partial class GitHubJsonContext : JsonSerializerContext;
