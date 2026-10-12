using ZeroAlloc.Results;

namespace Daedalus.Infrastructure.Services.GitHub;

/// <summary>
///     Acts on a GitHub repository, for interactive agents and for the post-gate host actions, which run only after a
///     human approved. Each write is sent exactly once: GitHub has no idempotency keys, so a retried comment is a double
///     comment on someone's pull request, and a retried label or close is a visible action taken twice. A failure is a
///     failed result carrying a <see cref="GitHubError"/>; it must surface to the agent rather than be swallowed, because
///     otherwise the agent reports success to a person when the write did not happen.
/// </summary>
public interface IGitHubWriter
{
    /// <summary>Posts a comment to an issue or pull request. Success carries a short human-readable confirmation.</summary>
    Task<Result<string, GitHubError>> CommentAsync(RepoRef repo, int number, string body, CancellationToken ct = default);

    /// <summary>Adds a label to an issue or pull request. Success carries a short human-readable confirmation.</summary>
    Task<Result<string, GitHubError>> AddLabelAsync(RepoRef repo, int number, string label, CancellationToken ct = default);

    /// <summary>Closes an issue or pull request. Success carries a short human-readable confirmation.</summary>
    Task<Result<string, GitHubError>> CloseIssueAsync(RepoRef repo, int number, CancellationToken ct = default);

    /// <summary>Files a new issue. Success carries the number GitHub assigned and its web link.</summary>
    Task<Result<CreatedIssue, GitHubError>> CreateIssueAsync(RepoRef repo, string title, string body, CancellationToken ct = default);
}
