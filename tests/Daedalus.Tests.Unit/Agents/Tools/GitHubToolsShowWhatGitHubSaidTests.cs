using Daedalus.Agents.Tools;
using Daedalus.Infrastructure.Services.GitHub;
using Microsoft.Extensions.Options;

namespace Daedalus.Tests.Unit.Agents.Tools;

/// <summary>
///     Phase 2.11, ruling P9: every agent tool that prints a GitHub failure shows what GitHub said, through
///     <see cref="GitHubError.ForAgent"/>. <c>Get</c> and <c>CommentOnIssue</c> have their own tests; these are the other five.
/// </summary>
public sealed class GitHubToolsShowWhatGitHubSaidTests
{
    private const string Said = "zqx-github-said";

    private static readonly GitHubError Refusal =
        GitHubError.FromStatus(403, GitHubErrorKind.Forbidden, null) with { GitHubSaid = Said };

    /// <summary>
    ///     Red: render <c>.Error.Message</c> instead of <c>.Error.ForAgent()</c> in <c>DaedalusIssueTools.Search</c>; the
    ///     search row fails.
    ///     Red: render <c>.Error.Message</c> instead in <c>DaedalusRepoTools.RepoDefaultBranch</c>; the default-branch row
    ///     fails.
    ///     Red: render <c>.Error.Message</c> instead in <c>DaedalusRepoActionTools.AddLabel</c>; the label row fails.
    ///     Red: render <c>.Error.Message</c> instead in <c>DaedalusRepoActionTools.CloseIssue</c>; the close row fails.
    ///     Red: render <c>.Error.Message</c> instead in <c>DaedalusRepoActionTools.CreateIssue</c>; the create row fails.
    /// </summary>
    [Theory]
    [InlineData("search")]
    [InlineData("default-branch")]
    [InlineData("label")]
    [InlineData("close")]
    [InlineData("create-issue")]
    public async Task A_tool_shows_what_GitHub_said(string tool)
    {
        var output = await RunAsync(tool);

        output.Should().EndWith($"GitHub said: {Said}");
    }

    private static async Task<string> RunAsync(string tool)
    {
        var reader = Substitute.For<IGitHubReader>();
        reader.SearchIssuesAsync(Arg.Any<RepoRef>(), Arg.Any<string>(), Arg.Any<IssueSearchState>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<IssueHit>, GitHubError>.Failure(Refusal));
        reader.GetDefaultBranchAsync(Arg.Any<RepoRef>(), Arg.Any<CancellationToken>())
            .Returns(Result<string, GitHubError>.Failure(Refusal));

        var writer = Substitute.For<IGitHubWriter>();
        writer.AddLabelAsync(Arg.Any<RepoRef>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string, GitHubError>.Failure(Refusal));
        writer.CloseIssueAsync(Arg.Any<RepoRef>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result<string, GitHubError>.Failure(Refusal));
        writer.CreateIssueAsync(Arg.Any<RepoRef>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<CreatedIssue, GitHubError>.Failure(Refusal));

        var actions = new DaedalusRepoActionTools(writer);
        return tool switch
        {
            "search" => await new DaedalusIssueTools(reader).Search("owner/repo", "crash"),
            "default-branch" => await new DaedalusRepoTools(reader, TimeProvider.System, Options.Create(new GitHubOptions())).RepoDefaultBranch("owner/repo"),
            "label" => await actions.AddLabel("owner/repo", 7, "stale"),
            "close" => await actions.CloseIssue("owner/repo", 7),
            "create-issue" => await actions.CreateIssue("owner/repo", "A title", "A body"),
            _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, "not a tool of this test"),
        };
    }
}
