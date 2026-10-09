using Daedalus.Agents.Tools;
using Daedalus.Infrastructure.Services.GitHub;
using ZeroAlloc.Results;

namespace Daedalus.Tests.Unit.Agents.Tools;

public sealed class DaedalusRepoActionToolsTests
{
    /// <summary>Red: skip the blank-title check; the writer is then called.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_title_is_refused_without_calling_github(string title)
    {
        var writer = Substitute.For<IGitHubWriter>();

        var output = await new DaedalusRepoActionTools(writer).CreateIssue("owner/repo", title, "body");

        output.Should().StartWith("Could not file the issue");
        await writer.DidNotReceiveWithAnyArgs().CreateIssueAsync(default!, default!, default!, default);
    }

    /// <summary>Red: return a fixed text; the number and link assertions fail.</summary>
    [Fact]
    public async Task A_filed_issue_is_reported_with_its_number_and_link()
    {
        var writer = Substitute.For<IGitHubWriter>();
        writer.CreateIssueAsync(Arg.Any<RepoRef>(), "T", "B", Arg.Any<CancellationToken>())
            .Returns(Result<CreatedIssue>.Success(new CreatedIssue(12, new Uri("https://github.com/owner/repo/issues/12"))));

        var output = await new DaedalusRepoActionTools(writer).CreateIssue("owner/repo", "T", "B");

        output.Should().Contain("owner/repo#12").And.Contain("https://github.com/owner/repo/issues/12");
    }

    /// <summary>Red: drop the length check from the tool; the writer is then called.</summary>
    [Fact]
    public async Task A_title_over_the_limit_is_refused_without_calling_github()
    {
        var writer = Substitute.For<IGitHubWriter>();
        var title = new string('x', GitHubApi.MaxIssueTitleLength + 1);

        var output = await new DaedalusRepoActionTools(writer).CreateIssue("owner/repo", title, "body");

        output.Should().StartWith("Could not file the issue").And.Contain("256");
        await writer.DidNotReceiveWithAnyArgs().CreateIssueAsync(default!, default!, default!, default);
    }

    /// <summary>Red: pass <c>body</c> through unchanged; the writer then receives null and the call does not match.</summary>
    [Fact]
    public async Task A_null_body_is_sent_as_an_empty_string()
    {
        var writer = Substitute.For<IGitHubWriter>();
        writer.CreateIssueAsync(Arg.Any<RepoRef>(), "T", "", Arg.Any<CancellationToken>())
            .Returns(Result<CreatedIssue>.Success(new CreatedIssue(3, new Uri("https://github.com/owner/repo/issues/3"))));

        var output = await new DaedalusRepoActionTools(writer).CreateIssue("owner/repo", "T", null!);

        output.Should().Contain("owner/repo#3");
    }
}
