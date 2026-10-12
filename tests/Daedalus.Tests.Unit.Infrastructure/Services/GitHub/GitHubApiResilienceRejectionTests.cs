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
    /// </summary>
    [Theory]
    [InlineData("timeout", GitHubErrorKind.Timeout)]
    [InlineData("circuit", GitHubErrorKind.Transport)]
    public async Task A_rejected_read_is_a_failed_result(string rejection, GitHubErrorKind kind)
    {
        var result = await Api(rejection).GetDefaultBranchAsync(Repo);

        result.Error.Kind.Should().Be(kind);
    }

    /// <summary>
    ///     Red: delete the <c>TimeoutRejectedException</c> catch in <c>WriteAsync</c>; the timeout row reads Transport.
    ///     Red: delete the <c>ExecutionRejectedException</c> catch in <c>WriteAsync</c>; the circuit row throws.
    ///     Red: drop <c>.ForWrite()</c> from <c>WriteAsync</c>'s timeout failure; the timeout row lacks the hint.
    ///     Red: add <c>.ForWrite()</c> to <c>WriteAsync</c>'s rejection failure; the circuit row carries the hint.
    ///     Red: return <c>NotSent</c> instead of <c>Rejected</c> from <c>WriteAsync</c>'s rejection failure; the circuit row
    ///     does not say the request was not sent.
    /// </summary>
    [Theory]
    [InlineData("timeout", GitHubErrorKind.Timeout, true)]
    [InlineData("circuit", GitHubErrorKind.Transport, false)]
    public async Task A_rejected_write_says_it_may_have_landed_only_when_it_was_sent(
        string rejection, GitHubErrorKind kind, bool mayHaveLanded)
    {
        var result = await Api(rejection).CommentAsync(Repo, 7, "a note");

        result.Error.Kind.Should().Be(kind);
        if (mayHaveLanded)
            result.Error.Message.Should().Contain(GitHubError.MayHaveLanded);
        else
        {
            result.Error.Message.Should().NotContain(GitHubError.MayHaveLanded);
            result.Error.Message.Should().Be(GitHubError.RejectedMessage);
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
