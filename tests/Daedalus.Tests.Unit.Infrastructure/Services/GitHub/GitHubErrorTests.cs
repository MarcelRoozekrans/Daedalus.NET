using Daedalus.Infrastructure.Services.GitHub;

namespace Daedalus.Tests.Unit.Infrastructure.Services.GitHub;

/// <summary>Phase 2.11: what a <see cref="GitHubError"/> says. Its message reaches agents, run errors and logs.</summary>
public sealed class GitHubErrorTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 11, 14, 5, 0, TimeSpan.Zero);

    /// <summary>Red, per row: change that kind's sentence in <c>GitHubError.Describe</c>; that row fails.</summary>
    [Theory]
    [InlineData(GitHubErrorKind.NotFound, 404, "GitHub answered 404: it does not exist, or the configured token cannot see it.")]
    [InlineData(GitHubErrorKind.Unauthorized, 401, "GitHub answered 401: the configured token was not accepted.")]
    [InlineData(GitHubErrorKind.Forbidden, 403, "GitHub answered 403: the configured token may not do this.")]
    [InlineData(GitHubErrorKind.Validation, 422, "GitHub answered 422: it refused the request as invalid.")]
    [InlineData(GitHubErrorKind.Conflict, 409, "GitHub answered 409: the request conflicts with the repository's current state.")]
    [InlineData(GitHubErrorKind.Timeout, 408, "GitHub answered 408: the request timed out.")]
    [InlineData(GitHubErrorKind.Server, 502, "GitHub answered 502: a server error.")]
    [InlineData(GitHubErrorKind.RateLimited, 429, "GitHub answered 429: rate-limited, and it gave no time to retry.")]
    public void A_status_error_says_what_GitHub_answered(GitHubErrorKind kind, int status, string expected)
    {
        var error = GitHubError.FromStatus(status, kind, null);

        error.Message.Should().Be(expected);
    }

    /// <summary>
    ///     Red: format the time with <c>"HH:mm:ss"</c>; the message fails.
    ///     Red: drop <c>retryAt</c> in <c>FromStatus</c>; the RetryAt assertion fails.
    /// </summary>
    [Fact]
    public void A_rate_limit_says_until_when()
    {
        var error = GitHubError.FromStatus(403, GitHubErrorKind.RateLimited, At);

        error.Message.Should().Be("GitHub answered 403: rate-limited until 14:05 UTC.");
        error.RetryAt.Should().Be(At);
    }

    /// <summary>Red: keep <c>retryAt</c> for every kind in <c>FromStatus</c>; the server error then carries one.</summary>
    [Fact]
    public void Only_a_rate_limit_keeps_a_retry_time()
    {
        GitHubError.FromStatus(503, GitHubErrorKind.Server, At).RetryAt.Should().BeNull();
    }

    /// <summary>
    ///     Red: give <c>Refused</c> kind <c>Unreadable</c>; the kind assertion fails.
    ///     Red: give it a status of 0; the status assertion fails.
    ///     Red: rewrite its message; the message assertion fails.
    /// </summary>
    [Fact]
    public void A_refusal_before_sending_keeps_Daedalus_own_text_and_has_no_status()
    {
        var error = GitHubError.Refused("An issue number must be positive.");

        error.Kind.Should().Be(GitHubErrorKind.Validation);
        error.StatusCode.Should().BeNull();
        error.Message.Should().Be("An issue number must be positive.");
    }

    /// <summary>Red: return the record's generated text from <c>ToString</c>; it then starts with "GitHubError {".</summary>
    [Fact]
    public void ToString_is_the_message()
    {
        GitHubError.TimedOut().ToString().Should().Be("The request to GitHub timed out.");
    }

    /// <summary>
    ///     What GitHub said reaches the agent only. ToString feeds logs and run errors through interpolation.
    ///     Red: return <c>ForAgent()</c> from <c>ToString</c>; the ToString assertion fails.
    ///     Red: make <c>Message</c> a computed property that appends <c>GitHubSaid</c>; the Message assertion fails.
    /// </summary>
    [Fact]
    public void What_GitHub_said_is_in_neither_the_message_nor_ToString()
    {
        var error = GitHubError.FromStatus(403, GitHubErrorKind.Forbidden, null) with { GitHubSaid = "zqx-said" };

        error.ToString().Should().NotContain("zqx-said");
        error.Message.Should().NotContain("zqx-said");
    }

    /// <summary>
    ///     Red: leave <c>GitHubSaid</c> out of <c>ForAgent</c>; the first assertion fails.
    ///     Red: add " GitHub said: " with nothing after it when <c>GitHubSaid</c> is null; the second assertion fails.
    /// </summary>
    [Fact]
    public void ForAgent_adds_what_GitHub_said_when_there_is_something()
    {
        var said = GitHubError.FromStatus(403, GitHubErrorKind.Forbidden, null) with { GitHubSaid = "Issue is locked." };

        said.ForAgent().Should().Be("GitHub answered 403: the configured token may not do this. GitHub said: Issue is locked.");
        GitHubError.TimedOut().ForAgent().Should().Be("The request to GitHub timed out.");
    }

    /// <summary>
    ///     Red: drop <c>Server</c> from <c>ForWrite</c>'s kinds; the server row fails.
    ///     Red: drop <c>Transport</c>; the transport row fails. Red: drop <c>Timeout</c>; the timeout row fails.
    ///     Red: drop <c>Unreadable</c>: GitHub answered 2xx but the answer could not be read, so the write may have landed.
    ///     Red: append the sentence to every kind; the forbidden row and the every-other-kind test fail.
    ///     Red: add <c>Rejected</c> to <c>ForWrite</c>'s kinds; the rejected row fails.
    /// </summary>
    [Theory]
    [InlineData(GitHubErrorKind.Timeout, true)]
    [InlineData(GitHubErrorKind.Server, true)]
    [InlineData(GitHubErrorKind.Transport, true)]
    [InlineData(GitHubErrorKind.Unreadable, true)]
    [InlineData(GitHubErrorKind.Forbidden, false)]
    [InlineData(GitHubErrorKind.Rejected, false)]
    public void A_write_that_may_have_landed_says_so(GitHubErrorKind kind, bool mayHaveLanded)
    {
        var error = kind switch
        {
            GitHubErrorKind.Timeout => GitHubError.TimedOut(),
            GitHubErrorKind.Transport => GitHubError.NotSent(),
            GitHubErrorKind.Unreadable => GitHubError.Unreadable("GitHub's answer could not be read.", 200),
            GitHubErrorKind.Rejected => GitHubError.Rejected(),
            GitHubErrorKind.Server => GitHubError.FromStatus(502, kind, null),
            _ => GitHubError.FromStatus(403, kind, null),
        };

        error.ForWrite().Message.Contains(GitHubError.MayHaveLanded, StringComparison.Ordinal).Should().Be(mayHaveLanded);
    }

    /// <summary>Red: add any other kind, for example <c>Conflict</c>, to <c>ForWrite</c>'s kinds; that kind fails.</summary>
    [Fact]
    public void A_write_that_was_refused_does_not_say_it_may_have_landed()
    {
        GitHubErrorKind[] mayHaveLanded = [GitHubErrorKind.Timeout, GitHubErrorKind.Server, GitHubErrorKind.Transport, GitHubErrorKind.Unreadable];

        var refused = Enum.GetValues<GitHubErrorKind>().Except(mayHaveLanded).ToArray();

        foreach (var kind in refused)
        {
            GitHubError.FromStatus(403, kind, null).ForWrite().Message.Should().NotContain(GitHubError.MayHaveLanded, $"{kind} is a refusal");
        }
    }

    /// <summary>
    ///     Red: give <c>NoToken</c> kind <c>Forbidden</c>; the NoToken kind fails. Red: give it a status; its status fails.
    ///     Red: swap <c>Unreadable</c>'s message and status; the Unreadable assertions fail.
    ///     Red: default <c>Unreadable</c>'s status parameter to 0 instead of null; the last assertion fails.
    /// </summary>
    [Fact]
    public void A_missing_token_and_an_unreadable_answer_keep_their_text_kind_and_status()
    {
        var noToken = GitHubError.NoToken("No GitHub token is configured.");
        var unreadable = GitHubError.Unreadable("GitHub's answer could not be read.", 200);

        noToken.Kind.Should().Be(GitHubErrorKind.Unauthorized);
        noToken.StatusCode.Should().BeNull();
        noToken.Message.Should().Be("No GitHub token is configured.");
        unreadable.Kind.Should().Be(GitHubErrorKind.Unreadable);
        unreadable.StatusCode.Should().Be(200);
        unreadable.Message.Should().Be("GitHub's answer could not be read.");
        GitHubError.Unreadable("x").StatusCode.Should().BeNull();
    }
}
