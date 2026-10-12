using Daedalus.Infrastructure.Services.GitHub;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Daedalus.Tests.Unit.Infrastructure.Services.GitHub;

/// <summary>
///     Phase 2.11, ruling P13: a refusal of the resilience pipeline, which no generated client maps, is a failed
///     <see cref="GitHubError"/> too. The handler stands in for the pipeline by throwing what its strategies throw.
/// </summary>
public sealed class GitHubApiResilienceRejectionTests
{
    private static readonly RepoRef Repo = RepoRef.Parse("owner/repo").Value;

    /// <summary>
    ///     Red: delete the <c>TimeoutRejectedException</c> catch in <c>CallAsync</c>; the timeout row reads Transport.
    ///     Red: delete the <c>ExecutionRejectedException</c> catch in <c>CallAsync</c>; the circuit row throws.
    ///     Red: return <c>NotSent</c> from that catch; the circuit row reads Transport.
    /// </summary>
    [Theory]
    [InlineData("timeout", GitHubErrorKind.Timeout)]
    [InlineData("circuit", GitHubErrorKind.Rejected)]
    public async Task A_rejected_read_is_a_failed_result(string rejection, GitHubErrorKind kind)
    {
        var result = await Api(rejection).GetDefaultBranchAsync(Repo);

        result.Error.Kind.Should().Be(kind);
    }

    /// <summary>
    ///     Every write goes through the rejection catch of <c>CallAsync</c> or <c>WriteAsync</c>; the creates then pass
    ///     the error through <c>ForWrite</c>, which must leave a rejection alone.
    ///     Red: delete the <c>TimeoutRejectedException</c> catch in <c>WriteAsync</c> or <c>CallAsync</c>; a timeout row reads Transport.
    ///     Red: delete the <c>ExecutionRejectedException</c> catch in <c>WriteAsync</c> or <c>CallAsync</c>; a circuit row throws.
    ///     Red: drop <c>.ForWrite()</c> from the timeout failure of <c>WriteAsync</c>; the comment timeout row lacks the hint.
    ///     Red: add <c>Rejected</c> to <c>ForWrite</c>'s kinds; every circuit row carries the hint.
    ///     Red: return <c>NotSent</c> instead of <c>Rejected</c> from either catch; the circuit rows read Transport.
    ///     Red: drop <c>.ForWrite()</c> from the failure of <c>CreateIssueAsync</c>; the issue timeout row lacks the hint.
    ///     Red: drop <c>.ForWrite()</c> from the failure of <c>CreatePullRequestAsync</c>; the pull request timeout row lacks the hint.
    /// </summary>
    [Theory]
    [InlineData("comment", "timeout", GitHubErrorKind.Timeout, true)]
    [InlineData("comment", "circuit", GitHubErrorKind.Rejected, false)]
    [InlineData("issue", "timeout", GitHubErrorKind.Timeout, true)]
    [InlineData("issue", "circuit", GitHubErrorKind.Rejected, false)]
    [InlineData("pull request", "timeout", GitHubErrorKind.Timeout, true)]
    [InlineData("pull request", "circuit", GitHubErrorKind.Rejected, false)]
    public async Task A_rejected_write_says_it_may_have_landed_only_when_it_was_sent(
        string write, string rejection, GitHubErrorKind kind, bool mayHaveLanded)
    {
        var api = Api(rejection);

        var error = write switch
        {
            "comment" => (await api.CommentAsync(Repo, 7, "a note")).Error,
            "issue" => (await api.CreateIssueAsync(Repo, "a title", "a body")).Error,
            _ => (await api.CreatePullRequestAsync(Repo, "feature", "main", "a title", "a body")).Error,
        };

        error.Kind.Should().Be(kind);
        if (mayHaveLanded)
            error.Message.Should().Contain(GitHubError.MayHaveLanded);
        else
        {
            error.Message.Should().NotContain(GitHubError.MayHaveLanded);
            error.Message.Should().Be(GitHubError.RejectedMessage);
        }
    }

    private static GitHubApi Api(string rejection)
    {
        Exception thrown = rejection switch
        {
            "timeout" => new TimeoutRejectedException(),
            _ => new BrokenCircuitException(),
        };
        var handler = new Rejecting(thrown);
        var clients = Substitute.For<IHttpClientFactory>();
        clients.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));
        return new GitHubApi(
            clients, Options.Create(new GitHubOptions()), new GitHubTokenSource(_ => "ghp_example"),
            TimeProvider.System, NullLogger<GitHubApi>.Instance);
    }

    /// <summary>Throws what a resilience strategy throws when it refuses or abandons a request.</summary>
    private sealed class Rejecting(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }
}
