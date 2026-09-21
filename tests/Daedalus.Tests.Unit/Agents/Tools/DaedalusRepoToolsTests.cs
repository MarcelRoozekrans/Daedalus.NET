using Daedalus.Infrastructure.Services.GitHub;
using Daedalus.Agents.Tools;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Daedalus.Tests.Unit.Agents.Tools;

public class DaedalusRepoToolsTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 19, 7, 0, 0, TimeSpan.Zero);

    private static DaedalusRepoTools CreateTools(IGitHubReader reader) =>
        new(reader, new FakeTimeProvider(FixedNow), Options.Create(new GitHubOptions()));

    [Fact]
    public async Task An_unchecked_category_is_stated_in_the_output_not_omitted()
    {
        var reader = Substitute.For<IGitHubReader>();
        reader.GetActivityAsync(Arg.Any<RepoRef>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new RepoActivity(
                RepoRef.Parse("owner/repo").Value,
                new DateTime(2026, 9, 18, 7, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 9, 19, 7, 0, 0, DateTimeKind.Utc),
                CategoryResult<CommitSummary>.Ok([], false),
                CategoryResult<PullRequestSummary>.Ok([], false),
                CategoryResult<PullRequestSummary>.Ok([], false),
                CategoryResult<IssueSummary>.Ok([], false),
                CategoryResult<WorkflowRunSummary>.Failed("500 from the actions endpoint")));

        var output = await CreateTools(reader).RepoActivity("owner/repo", null);

        output.Should().ContainEquivalentOf("could not");
        output.Should().ContainEquivalentOf("CI");
        output.Should().NotContainEquivalentOf("no failed CI runs",
            "a category we could not read must never be reported as clean");
    }

    [Fact]
    public async Task The_window_actually_covered_is_stated()
    {
        var reader = StubActivityReader();

        var output = await CreateTools(reader).RepoActivity("owner/repo", null);

        output.Should().Contain("2026-09-18");
        output.Should().Contain("2026-09-19");
    }

    [Fact]
    public async Task Truncation_is_stated()
    {
        var reader = Substitute.For<IGitHubReader>();
        reader.GetActivityAsync(Arg.Any<RepoRef>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(ActivityWith(CategoryResult<CommitSummary>.Ok(
                [new CommitSummary("abc1234", "a change", "someone", new DateTime(2026, 9, 19, 6, 0, 0, DateTimeKind.Utc))],
                truncated: true)));

        var output = await CreateTools(reader).RepoActivity("owner/repo", null);

        output.Should().ContainEquivalentOf("more than");
    }

    [Fact]
    public async Task A_truncated_and_empty_category_is_reported_as_incomplete_not_clean()
    {
        var reader = Substitute.For<IGitHubReader>();
        reader.GetActivityAsync(Arg.Any<RepoRef>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(ActivityWith(CategoryResult<CommitSummary>.Ok([], truncated: true)));

        var output = await CreateTools(reader).RepoActivity("owner/repo", null);

        output.Should().NotContainEquivalentOf("no commits",
            "a page that filled the ceiling and filtered down to zero is not the same thing as a repository " +
            "that had nothing happen — reporting it as clean discards the truncation signal");
        output.Should().ContainEquivalentOf("commits",
            "the category must still say something happened worth investigating further");
    }

    [Fact]
    public async Task A_malformed_repository_argument_is_refused_without_calling_the_reader()
    {
        var reader = Substitute.For<IGitHubReader>();

        var output = await CreateTools(reader).RepoActivity("not-a-repo", null);

        output.Should().ContainEquivalentOf("owner/name");
        await reader.DidNotReceiveWithAnyArgs().GetActivityAsync(default!, default, default);
    }

    [Fact]
    public async Task A_write_failure_is_reported_to_the_agent_rather_than_swallowed()
    {
        var writer = Substitute.For<IGitHubWriter>();
        writer.CommentAsync(Arg.Any<RepoRef>(), 7, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Failure("422 Validation Failed"));

        var output = await new DaedalusRepoActionTools(writer).CommentOnIssue("owner/repo", 7, "a note");

        output.Should().ContainEquivalentOf("422");
    }

    private static RepoActivity ActivityWith(CategoryResult<CommitSummary> commits) =>
        new(
            RepoRef.Parse("owner/repo").Value,
            new DateTime(2026, 9, 18, 7, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 19, 7, 0, 0, DateTimeKind.Utc),
            commits,
            CategoryResult<PullRequestSummary>.Ok([], false),
            CategoryResult<PullRequestSummary>.Ok([], false),
            CategoryResult<IssueSummary>.Ok([], false),
            CategoryResult<WorkflowRunSummary>.Ok([], false));

    private static IGitHubReader StubActivityReader()
    {
        var reader = Substitute.For<IGitHubReader>();
        reader.GetActivityAsync(Arg.Any<RepoRef>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(ActivityWith(CategoryResult<CommitSummary>.Ok([], false)));
        return reader;
    }
}
