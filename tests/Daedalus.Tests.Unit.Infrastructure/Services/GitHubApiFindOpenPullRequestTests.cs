using System.Net;
using Daedalus.Infrastructure.Services.GitHub;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Daedalus.Tests.Unit.Infrastructure.Services;

/// <summary>
///     <see cref="GitHubApi.FindOpenPullRequestAsync"/>: the open pull request for one head branch, or none. The
///     branch a manufacture run pushes has a slash in it, so the <c>head</c> filter has to be URL-encoded whole.
/// </summary>
public sealed class GitHubApiFindOpenPullRequestTests
{
    private static RepoRef Repo() => RepoRef.Parse("o/r").Value;

    private static GitHubApi Build(RespondingHandler handler) => new(
        new HttpClient(handler),
        Options.Create(new GitHubOptions()),
        new GitHubTokenSource(_ => "ghp_example"),
        TimeProvider.System,
        NullLogger<GitHubApi>.Instance);

    /// <summary>
    ///     Falsifiable per assertion: failing on a list that is not an array of one fails the first, returning
    ///     <see langword="null"/> for a non-empty list fails the second, reading <c>url</c> instead of
    ///     <c>html_url</c> fails the third, and reading anything but <c>number</c> fails the fourth.
    /// </summary>
    [Fact]
    public async Task An_open_pull_request_is_returned_with_its_web_url()
    {
        var handler = new RespondingHandler(HttpStatusCode.OK, """[{"html_url":"https://github.com/o/r/pull/7","url":"https://api.github.com/repos/o/r/pulls/7","number":7}]""");

        var result = await Build(handler).FindOpenPullRequestAsync(Repo(), "manufacture/abc", CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error : null);
        result.Value.Should().NotBeNull("an open pull request was listed");
        result.Value!.WebUrl.Should().Be("https://github.com/o/r/pull/7");
        result.Value.PullRequestId.Should().Be("7");
    }

    /// <summary>
    ///     Falsifiable per assertion: omitting the <c>head</c> filter fails both, and interpolating
    ///     <c>{owner}:{branch}</c> without <see cref="Uri.EscapeDataString(string)"/> fails the raw one, because the
    ///     slash in the branch would then be sent unencoded.
    /// </summary>
    [Fact]
    public async Task The_head_filter_is_the_url_encoded_owner_and_branch()
    {
        var handler = new RespondingHandler(HttpStatusCode.OK, "[]");

        await Build(handler).FindOpenPullRequestAsync(Repo(), "manufacture/abc", CancellationToken.None);

        var query = handler.LastRequest!.RequestUri!.Query;
        query.Should().Be("?state=open&head=o%3Amanufacture%2Fabc");
        Uri.UnescapeDataString(query).Should().Be("?state=open&head=o:manufacture/abc");
    }

    /// <summary>Falsifiable: treating an empty list as a failure, or as a pull request, turns this red.</summary>
    [Fact]
    public async Task An_empty_list_means_no_open_pull_request()
    {
        var handler = new RespondingHandler(HttpStatusCode.OK, "[]");

        var result = await Build(handler).FindOpenPullRequestAsync(Repo(), "manufacture/abc", CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error : null);
        result.Value.Should().BeNull();
    }

    /// <summary>Falsifiable: ignoring the status code and parsing the error body as an empty list turns this red.</summary>
    [Fact]
    public async Task An_unauthorized_response_is_a_failure()
    {
        var handler = new RespondingHandler(HttpStatusCode.Unauthorized, """{"message":"Bad credentials"}""");

        var result = await Build(handler).FindOpenPullRequestAsync(Repo(), "manufacture/abc", CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Bad credentials");
    }

    /// <summary>Records the last request and answers every request with one fixed response.</summary>
    private sealed class RespondingHandler(HttpStatusCode status, string json) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
