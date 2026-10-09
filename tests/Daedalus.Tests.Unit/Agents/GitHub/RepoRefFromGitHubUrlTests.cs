using Daedalus.Infrastructure.Services.GitHub;

namespace Daedalus.Tests.Unit.Agents.GitHub;

/// <summary>
///     Phase 2.7: the owner and name out of a github.com URL, used to find the repository a run's pull request is in and
///     to re-check it against the allow-list.
/// </summary>
public sealed class RepoRefFromGitHubUrlTests
{
    /// <summary>Red: drop the <c>.git</c> strip, or the SSH branch; the matching rows fail.</summary>
    [Theory]
    [InlineData("https://github.com/MarcelRoozekrans/daedalus-sandbox.git")]
    [InlineData("https://github.com/MarcelRoozekrans/daedalus-sandbox")]
    [InlineData("git@github.com:MarcelRoozekrans/daedalus-sandbox.git")]
    [InlineData("https://github.com/MarcelRoozekrans/daedalus-sandbox/pull/7")]
    public void A_github_remote_or_link_yields_owner_and_name(string url)
    {
        var parsed = RepoRef.FromGitHubUrl(url);

        parsed.IsSuccess.Should().BeTrue(parsed.IsFailure ? parsed.Error : "");
        parsed.Value.ToString().Should().Be("MarcelRoozekrans/daedalus-sandbox");
    }

    /// <summary>Red: accept any host; the gitlab row then parses.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://gitlab.com/owner/repo")]
    [InlineData("https://github.com/owner")]
    [InlineData("not a url")]
    public void Anything_else_is_a_failure_not_a_guess(string? url) =>
        RepoRef.FromGitHubUrl(url).IsFailure.Should().BeTrue();
}
