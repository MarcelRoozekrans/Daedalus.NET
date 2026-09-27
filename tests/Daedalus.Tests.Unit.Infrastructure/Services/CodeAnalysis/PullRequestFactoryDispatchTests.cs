using System.Net;
using Daedalus.Application.Services.CodeAnalysis;
using Daedalus.Domain.CodeAnalysis;
using Daedalus.Infrastructure.Services.CodeAnalysis;
using Daedalus.Infrastructure.Services.GitHub;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Daedalus.Tests.Unit.Infrastructure.Services.CodeAnalysis;

/// <summary>
///     Guards <see cref="PullRequestFactory"/>'s platform dispatch: a GitHub repository URL must reach
///     <see cref="GitHubPullRequestFactory"/> and an Azure DevOps repository URL must reach
///     <see cref="AzureDevOpsPullRequestFactory"/> — never the other way around, and never both.
/// </summary>
/// <remarks>
///     Written and run against the codebase BEFORE any consolidation of the GitHub HTTP client, as the guard for
///     that work: if consolidating <c>GitHubApi</c> and <see cref="GitHubPullRequestFactory"/>
///     ever routed a request across the <see cref="IPullRequestFactory"/> dispatch boundary instead of underneath it —
///     for example by making <see cref="PullRequestFactory"/> call the GitHub path for every platform, or by deleting
///     the Azure DevOps branch of its switch — this test fails: the wrong recorder would observe the call, or neither
///     would, or both would.
/// </remarks>
public sealed class PullRequestFactoryDispatchTests
{
    private static IRepositoryAuthenticationProvider AuthProviderReturningToken() =>
        FakeAuthProvider.WithToken("a-token");

    [Fact]
    public async Task A_github_url_reaches_the_github_factory_and_never_the_azure_devops_one()
    {
        var githubHandler = new RecordingHandler(HttpStatusCode.ServiceUnavailable);
        var azureHandler = new RecordingHandler(HttpStatusCode.ServiceUnavailable);
        var sut = BuildSut(githubHandler, azureHandler);

        await sut.CreatePullRequestAsync(
            "https://github.com/owner/repo", "feature", "main", "title", "description");

        githubHandler.LastRequestUri.Should().NotBeNull("a GitHub URL must reach the GitHub factory's HTTP client");
        githubHandler.LastRequestUri!.Host.Should().Be("api.github.com");
        azureHandler.LastRequestUri.Should().BeNull("a GitHub URL must never reach the Azure DevOps factory");
    }

    [Fact]
    public async Task An_azure_devops_url_reaches_the_azure_devops_factory_and_never_the_github_one()
    {
        var githubHandler = new RecordingHandler(HttpStatusCode.ServiceUnavailable);
        var azureHandler = new RecordingHandler(HttpStatusCode.ServiceUnavailable);
        var sut = BuildSut(githubHandler, azureHandler);

        await sut.CreatePullRequestAsync(
            "https://dev.azure.com/org/project/_git/repo", "feature", "main", "title", "description");

        azureHandler.LastRequestUri.Should().NotBeNull("an Azure DevOps URL must reach the Azure DevOps factory's HTTP client");
        azureHandler.LastRequestUri!.Host.Should().Be("dev.azure.com");
        githubHandler.LastRequestUri.Should().BeNull("an Azure DevOps URL must never reach the GitHub factory");
    }

    /// <summary>
    ///     The open-pull-request lookup goes through the same dispatch. Falsifiable per assertion: routing the lookup
    ///     away from the GitHub factory fails the first, and parsing the wrong owner or repository out of the URL,
    ///     or querying anything but the pulls endpoint, fails the second.
    /// </summary>
    [Fact]
    public async Task A_github_url_is_looked_up_on_the_github_pulls_endpoint()
    {
        var githubHandler = new RecordingHandler(HttpStatusCode.ServiceUnavailable);
        var azureHandler = new RecordingHandler(HttpStatusCode.ServiceUnavailable);
        var sut = BuildSut(githubHandler, azureHandler);

        await sut.FindOpenPullRequestAsync("https://github.com/owner/repo.git", "manufacture/abc");

        githubHandler.LastRequestUri.Should().NotBeNull("a GitHub URL must reach the GitHub factory's HTTP client");
        githubHandler.LastRequestUri!.AbsolutePath.Should().Be("/repos/owner/repo/pulls");
    }

    /// <summary>
    ///     Azure DevOps has no lookup yet, and says so rather than answering "none open". Falsifiable per assertion:
    ///     returning <c>Success(null)</c> fails the first, and routing the URL to the GitHub factory, whose URL
    ///     parser then fails with its own message, fails the second.
    /// </summary>
    [Fact]
    public async Task An_azure_devops_lookup_fails_as_unsupported()
    {
        var githubHandler = new RecordingHandler(HttpStatusCode.ServiceUnavailable);
        var azureHandler = new RecordingHandler(HttpStatusCode.ServiceUnavailable);
        var sut = BuildSut(githubHandler, azureHandler);

        var result = await sut.FindOpenPullRequestAsync("https://dev.azure.com/org/project/_git/repo", "manufacture/abc");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("finding an open pull request is not supported for Azure DevOps");
    }

    private static PullRequestFactory BuildSut(RecordingHandler githubHandler, RecordingHandler azureHandler)
    {
        var authProvider = AuthProviderReturningToken();

        // GitHubPullRequestFactory now delegates to GitHubApi rather than owning an HttpClient itself (Task 5's
        // consolidation) — wired over the same recording handler, so the assertion below still observes the real
        // outbound request rather than anything about how the factory gets there.
        var gitHubApi = new GitHubApi(
            new HttpClient(githubHandler), Options.Create(new GitHubOptions()),
            new GitHubTokenSource(_ => "a-token"), TimeProvider.System, NullLogger<GitHubApi>.Instance);
        var github = new GitHubPullRequestFactory(gitHubApi, NullLogger<GitHubPullRequestFactory>.Instance);

        var azure = new AzureDevOpsPullRequestFactory(
            new HttpClient(azureHandler), authProvider, NullLogger<AzureDevOpsPullRequestFactory>.Instance);

        var detector = new RepositoryPlatformDetector(NullLogger<RepositoryPlatformDetector>.Instance);

        return new PullRequestFactory(github, azure, detector, NullLogger<PullRequestFactory>.Instance);
    }

    /// <summary>Returns a fixed success token for every platform, so both factories get past authentication and reach their HTTP call.</summary>
    private static class FakeAuthProvider
    {
        public static IRepositoryAuthenticationProvider WithToken(string token)
        {
            var provider = Substitute.For<IRepositoryAuthenticationProvider>();
            provider.GetAuthTokenAsync(Arg.Any<RepositoryPlatform>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result<string>.Success(token)));
            return provider;
        }
    }

    /// <summary>Records the last request it saw and replies with a fixed status — no real network call is made.</summary>
    private sealed class RecordingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent("""{"message":"unavailable"}"""),
            });
        }
    }
}
