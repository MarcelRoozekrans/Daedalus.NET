using System.ComponentModel;
using Daedalus.Infrastructure.Services.GitHub;
using Thalos;

namespace Daedalus.Agents.Tools;

/// <summary>
///     Write access to a GitHub repository exposed to Thalos agents: <c>comment_on_issue</c>, <c>add_label</c>,
///     <c>close_issue</c> and <c>create_issue</c>.
/// </summary>
/// <remarks>
///     <b>Only <see cref="IGitHubWriter"/>.</b> This class cannot reach <see cref="IGitHubReader"/> or
///     <c>GitHubApi</c> directly. Pairing the reader and the writer in one class would quietly defeat the
///     read/write split this phase exists to establish — every method here takes a real, visible action against a
///     real repository, and a failure is surfaced to the caller rather than swallowed, because otherwise an agent
///     reports success to a person when the write never happened.
/// </remarks>
/// <param name="writer">The one seam this tool acts on a repository through.</param>
[ThalosToolType]
public sealed class DaedalusRepoActionTools(IGitHubWriter writer)
{
    /// <summary>Posts a comment to an issue or pull request.</summary>
    [ThalosTool("comment_on_issue")]
    [Description(
        "Post a comment on a GitHub issue or pull request. This modifies a real repository — the comment is " +
        "visible to everyone with access to it.")]
    public async Task<string> CommentOnIssue(
        [Description("The repository to act on, given as owner/name (e.g. octocat/hello-world).")] string repo,
        [Description("The issue or pull request number to comment on.")] int number,
        [Description("The comment body to post.")] string body,
        CancellationToken ct = default)
    {
        var parsedRepo = RepoRef.Parse(repo);
        if (parsedRepo.IsFailure)
            return parsedRepo.Error;

        // Escaped, so a comment this account writes can never end with a marker the file-findings action trusts.
        var result = await writer.CommentAsync(parsedRepo.Value, number, HtmlCommentText.Neutralize(body ?? ""), ct);
        return result.IsSuccess ? result.Value : $"Could not post the comment: {result.Error}";
    }

    /// <summary>Adds a label to an issue or pull request.</summary>
    [ThalosTool("add_label")]
    [Description(
        "Add a label to a GitHub issue or pull request. This modifies a real repository — the label is visible " +
        "to everyone with access to it.")]
    public async Task<string> AddLabel(
        [Description("The repository to act on, given as owner/name (e.g. octocat/hello-world).")] string repo,
        [Description("The issue or pull request number to label.")] int number,
        [Description("The label to add.")] string label,
        CancellationToken ct = default)
    {
        var parsedRepo = RepoRef.Parse(repo);
        if (parsedRepo.IsFailure)
            return parsedRepo.Error;

        var result = await writer.AddLabelAsync(parsedRepo.Value, number, label, ct);
        return result.IsSuccess ? result.Value : $"Could not add the label: {result.Error}";
    }

    /// <summary>Closes an issue or pull request.</summary>
    [ThalosTool("close_issue")]
    [Description(
        "Close a GitHub issue or pull request. This modifies a real repository — the closure is visible to " +
        "everyone with access to it.")]
    public async Task<string> CloseIssue(
        [Description("The repository to act on, given as owner/name (e.g. octocat/hello-world).")] string repo,
        [Description("The issue or pull request number to close.")] int number,
        CancellationToken ct = default)
    {
        var parsedRepo = RepoRef.Parse(repo);
        if (parsedRepo.IsFailure)
            return parsedRepo.Error;

        var result = await writer.CloseIssueAsync(parsedRepo.Value, number, ct);
        return result.IsSuccess ? result.Value : $"Could not close the issue: {result.Error}";
    }

    /// <summary>Files a new issue.</summary>
    [ThalosTool("create_issue")]
    [Description(
        "File a new GitHub issue. This modifies a real repository: the issue is visible to everyone with access to it. " +
        "Search for an existing issue first and comment on that one instead of filing a duplicate.")]
    public async Task<string> CreateIssue(
        [Description("The repository to act on, given as owner/name (e.g. octocat/hello-world).")] string repo,
        [Description("The issue title, at most 256 characters.")] string title,
        [Description("The issue body, in GitHub Markdown.")] string body,
        CancellationToken ct = default)
    {
        var parsedRepo = RepoRef.Parse(repo);
        if (parsedRepo.IsFailure)
            return parsedRepo.Error;

        if (string.IsNullOrWhiteSpace(title))
            return "Could not file the issue: a title is required.";

        // Escaped, so an issue this account files can never carry a marker the file-findings action trusts. The limit
        // applies to the title as posted, escapes included.
        var posted = HtmlCommentText.Neutralize(title.Trim());
        if (posted.Length > GitHubApi.MaxIssueTitleLength)
            return $"Could not file the issue: the title is longer than {GitHubApi.MaxIssueTitleLength} characters.";

        var result = await writer.CreateIssueAsync(parsedRepo.Value, posted, HtmlCommentText.Neutralize(body ?? ""), ct);
        return result.IsSuccess
            ? $"Filed {parsedRepo.Value}#{result.Value.Number}: {result.Value.HtmlUrl}"
            : $"Could not file the issue: {result.Error}";
    }
}
