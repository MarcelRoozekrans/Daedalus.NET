using System.Net;
using Daedalus.Infrastructure.Services.GitHub;
using Microsoft.Extensions.DependencyInjection;

namespace Daedalus.Tests.Unit.Infrastructure.Services.GitHub;

/// <summary>
///     Phase 2.11, decision D3 and defect 1: a write goes out exactly once through the real pipeline, on a 5xx and on a
///     timeout alike, while a read still retries, except a 429 (amendment A1). GitHub has no idempotency keys, so a
///     retried write can land twice. <c>AddGitHubApi</c> registers <c>TimeProvider.System</c>, so RetryAt is checked
///     against the real clock with a tolerance.
/// </summary>
public sealed class GitHubRetryTests
{
    private static readonly RepoRef Repo = RepoRef.Parse("owner/repo").Value;

    public static TheoryData<string> Writes => new() { "comment", "label", "close", "create-issue", "create-pull-request" };

    /// <summary>
    ///     Red: drop <c>NeverRetry</c> from the write client; each write is sent 4 times.
    ///     Red: map 503 to Validation in <c>GitHubErrorMapper</c>; the kind assertion fails.
    /// </summary>
    [Theory]
    [MemberData(nameof(Writes))]
    public async Task A_write_is_sent_once_when_GitHub_answers_503(string write)
    {
        var primary = RecordingHandler.Answering(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await using var host = GitHubHost.Build(primary);

        var error = await WriteAsync(host.GetRequiredService<GitHubApi>(), write);

        error.Kind.Should().Be(GitHubErrorKind.Server);
        primary.Count.Should().Be(1);
    }

    /// <summary>
    ///     Red: drop <c>NeverRetry</c> from the write client; each write is sent 4 times.
    ///     Red: drop the <c>TimeoutRejectedException</c> catch from <c>GitHubApi</c>; the timeout is caught as a generic
    ///     rejection and the kind is Transport.
    /// </summary>
    [Theory]
    [MemberData(nameof(Writes))]
    public async Task A_write_is_sent_once_when_the_attempt_times_out(string write)
    {
        var primary = new RecordingHandler((_, ct) => NeverAnswerAsync(ct));
        await using var host = GitHubHost.Build(primary, TimeSpan.FromMilliseconds(200));

        var error = await WriteAsync(host.GetRequiredService<GitHubApi>(), write);

        error.Kind.Should().Be(GitHubErrorKind.Timeout);
        primary.Count.Should().Be(1);
    }

    /// <summary>
    ///     Red: give the read client <c>NeverRetry</c> too; the read is sent once.
    ///     Red: map 503 to Validation in the mapper; the kind assertion fails.
    /// </summary>
    [Fact]
    public async Task A_read_is_still_retried_when_GitHub_answers_503()
    {
        var primary = RecordingHandler.Answering(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await using var host = GitHubHost.Build(primary);

        var read = await host.GetRequiredService<GitHubApi>().GetIssueAsync(Repo, 7);

        read.Error.Kind.Should().Be(GitHubErrorKind.Server);
        primary.Count.Should().Be(4);
    }

    /// <summary>
    ///     Red: give the read client <c>NeverRetry</c> too; the read is sent once.
    ///     Red: map a timeout to NotSent in the <c>GitHubApi.CallAsync</c> catch; the kind is Transport.
    /// </summary>
    [Fact]
    public async Task A_read_is_still_retried_after_a_timeout()
    {
        var primary = new RecordingHandler((_, ct) => NeverAnswerAsync(ct));
        await using var host = GitHubHost.Build(primary, TimeSpan.FromMilliseconds(200));

        var read = await host.GetRequiredService<GitHubApi>().GetIssueAsync(Repo, 7);

        read.Error.Kind.Should().Be(GitHubErrorKind.Timeout);
        primary.Count.Should().Be(4);
    }

    /// <summary>
    ///     Spec amendment A1, ruling P28: a read refused with 429 is sent once and says when to retry, rather than waiting
    ///     out Retry-After until the total timeout turns it into a Timeout.
    ///     Red: drop the <c>TooManyRequests</c> clause from <c>RetryReadsExceptRateLimits</c>; the read is sent 4 times.
    ///     Red: map 429 to Validation in the mapper; the kind assertion fails.
    ///     Red: drop <c>Retry-After</c> from the mapper's RetryAt; RetryAt falls back to now plus 60 seconds.
    /// </summary>
    [Fact]
    public async Task A_rate_limited_read_is_not_retried_and_says_when()
    {
        var primary = RecordingHandler.Answering(_ =>
        {
            var refused = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            refused.Headers.TryAddWithoutValidation("Retry-After", "1");
            return refused;
        });
        await using var host = GitHubHost.Build(primary);

        var read = await host.GetRequiredService<GitHubApi>().GetIssueAsync(Repo, 7);

        primary.Count.Should().Be(1);
        read.Error.Kind.Should().Be(GitHubErrorKind.RateLimited);
        read.Error.RetryAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddSeconds(1), TimeSpan.FromSeconds(5));
    }

    private static async Task<GitHubError> WriteAsync(GitHubApi api, string write) => write switch
    {
        "comment" => (await api.CommentAsync(Repo, 7, "a note")).Error,
        "label" => (await api.AddLabelAsync(Repo, 7, "stale")).Error,
        "close" => (await api.CloseIssueAsync(Repo, 7)).Error,
        "create-issue" => (await api.CreateIssueAsync(Repo, "A title", "A body")).Error,
        "create-pull-request" => (await api.CreatePullRequestAsync(Repo, "feature", "main", "a title", "a description")).Error,
        _ => throw new ArgumentOutOfRangeException(nameof(write), write, "not a write of this test"),
    };

    /// <summary>A GitHub that never answers: the attempt timeout cancels <paramref name="ct"/>.</summary>
    private static async Task<HttpResponseMessage> NeverAnswerAsync(CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        return new HttpResponseMessage(HttpStatusCode.OK);
    }
}
