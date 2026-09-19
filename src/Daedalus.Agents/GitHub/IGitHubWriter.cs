using CSharpFunctionalExtensions;

namespace Daedalus.Agents.GitHub;

/// <summary>
///     Acts on a GitHub repository — for interactive agents only. There is no retry anywhere in this path: a
///     retried comment is a double comment on someone's pull request, and a retried label or close is a visible
///     action taken twice. A failure returns <see cref="Result.Failure{T}(string)"/> and stops; it must surface to
///     the agent rather than be swallowed, because otherwise the agent will report success to a person when the
///     write did not happen.
/// </summary>
public interface IGitHubWriter
{
    /// <summary>Posts a comment to an issue or pull request. Success carries a short human-readable confirmation.</summary>
    Task<Result<string>> CommentAsync(RepoRef repo, int number, string body, CancellationToken ct = default);

    /// <summary>Adds a label to an issue or pull request. Success carries a short human-readable confirmation.</summary>
    Task<Result<string>> AddLabelAsync(RepoRef repo, int number, string label, CancellationToken ct = default);

    /// <summary>Closes an issue or pull request. Success carries a short human-readable confirmation.</summary>
    Task<Result<string>> CloseIssueAsync(RepoRef repo, int number, CancellationToken ct = default);
}
