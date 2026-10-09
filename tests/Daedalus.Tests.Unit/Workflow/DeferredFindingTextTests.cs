using Daedalus.Agents.Workflow;
using Daedalus.Infrastructure.Services.GitHub;

namespace Daedalus.Tests.Unit.Workflow;

public sealed class DeferredFindingTextTests
{
    private static readonly Guid RunId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Uri PrUrl = new("https://github.com/owner/repo/pull/7");
    private static readonly IdentifiedDeferredFinding Finding = new("correctness-1", "correctness",
        new DeferredFinding("src/Sub Dir/C#/A.cs", 42, "Cache never expires", "a stale entry is served", "different-area"));

    /// <summary>The marker is what the retry scan looks for. Red: change its format; the retry test in the action suite also fails.</summary>
    [Fact]
    public void The_marker_names_the_run_and_the_finding() =>
        DeferredFindingText.Marker(RunId, "correctness-1").Should().Be("<!-- daedalus-run:11111111-2222-3333-4444-555555555555 finding:correctness-1 -->");

    /// <summary>
    ///     Red: link to the branch instead of the head sha, and the permalink assertion fails. Red: skip path escaping,
    ///     and the permalink assertion fails on the space and the hash, which would otherwise start a fragment.
    /// </summary>
    [Fact]
    public void An_issue_body_carries_a_permalink_at_the_head_commit_the_reason_the_run_and_the_marker()
    {
        var body = DeferredFindingText.IssueBody(RepoRef.Parse("owner/repo").Value, "abc123", PrUrl, RunId, Finding, notUsed: null);

        body.Should().Contain("https://github.com/owner/repo/blob/abc123/src/Sub%20Dir/C%23/A.cs#L42");
        body.Should().Contain("a stale entry is served").And.Contain("in code this change does not touch");
        body.Should().Contain("https://github.com/owner/repo/pull/7").And.Contain(RunId.ToString());
        body.Should().EndWith(DeferredFindingText.Marker(RunId, "correctness-1"));
    }

    /// <summary>Red: drop the notUsed line; the reviewer's rejected pointer is then lost.</summary>
    [Fact]
    public void An_issue_body_says_why_the_reviewers_existing_issue_was_not_used() =>
        DeferredFindingText.IssueBody(RepoRef.Parse("owner/repo").Value, "abc", PrUrl, RunId, Finding, "#8 is closed")
            .Should().Contain("#8 is closed");
}
