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

    private static GitHubApi Build(HttpMessageHandler handler) => new(
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

    /// <summary>
    ///     A network or DNS fault is a failed result, not an exception. Falsifiable per assertion: removing the
    ///     catch makes the call throw, and <c>act</c> fails on <c>NotThrowAsync</c>; answering <c>Success(null)</c>
    ///     from the catch fails <c>IsFailure</c>; returning a failure without the exception's message fails the last.
    /// </summary>
    [Fact]
    public async Task A_network_fault_is_a_failure_not_an_exception()
    {
        var api = Build(new ThrowingHandler(new HttpRequestException("No such host is known.")));
        Result<Daedalus.Domain.CodeAnalysis.PullRequestResult?> result = default!;

        var act = async () => result = await api.FindOpenPullRequestAsync(Repo(), "manufacture/abc", CancellationToken.None);

        await act.Should().NotThrowAsync();
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("No such host is known.");
    }

    /// <summary>
    ///     A client-side timeout surfaces as an <see cref="OperationCanceledException"/> while the caller's token is
    ///     not cancelled, and is a failed result. Falsifiable: letting every <see cref="OperationCanceledException"/>
    ///     through, whatever the caller's token says, makes <c>act</c> throw, and answering
    ///     <c>Success(null)</c> from the catch fails <c>IsFailure</c>.
    /// </summary>
    [Fact]
    public async Task A_timeout_of_the_clients_own_is_a_failure()
    {
        var api = Build(new ThrowingHandler(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.")));
        Result<Daedalus.Domain.CodeAnalysis.PullRequestResult?> result = default!;

        var act = async () => result = await api.FindOpenPullRequestAsync(Repo(), "manufacture/abc", CancellationToken.None);

        await act.Should().NotThrowAsync();
        result.IsFailure.Should().BeTrue();
    }

    /// <summary>
    ///     A cancellation of the caller's own token still propagates. Falsifiable: catching every
    ///     <see cref="OperationCanceledException"/> as a failure turns this red.
    /// </summary>
    [Fact]
    public async Task A_cancellation_of_the_callers_token_propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var api = Build(new ThrowingHandler(new TaskCanceledException("canceled")));

        var act = async () => await api.FindOpenPullRequestAsync(Repo(), "manufacture/abc", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    ///     A 200 whose body is not JSON, such as a proxy's HTML page, is a failure. Falsifiable: removing the catch
    ///     makes <c>act</c> throw <see cref="System.Text.Json.JsonException"/>, and answering <c>Success(null)</c> from
    ///     the catch fails <c>IsFailure</c>.
    /// </summary>
    [Fact]
    public async Task A_body_that_is_not_json_is_a_failure()
    {
        var api = Build(new RespondingHandler(HttpStatusCode.OK, "<html><body>Proxy login</body></html>"));
        Result<Daedalus.Domain.CodeAnalysis.PullRequestResult?> result = default!;

        var act = async () => result = await api.FindOpenPullRequestAsync(Repo(), "manufacture/abc", CancellationToken.None);

        await act.Should().NotThrowAsync();
        result.IsFailure.Should().BeTrue();
    }

    /// <summary>
    ///     A listed pull request without <c>html_url</c> is a failure. Falsifiable: removing the catch makes
    ///     <c>act</c> throw <see cref="KeyNotFoundException"/>, and answering <c>Success(null)</c> from the catch fails
    ///     <c>IsFailure</c>.
    /// </summary>
    [Fact]
    public async Task A_listed_pull_request_missing_its_web_url_is_a_failure()
    {
        var api = Build(new RespondingHandler(HttpStatusCode.OK, """[{"number":7}]"""));
        Result<Daedalus.Domain.CodeAnalysis.PullRequestResult?> result = default!;

        var act = async () => result = await api.FindOpenPullRequestAsync(Repo(), "manufacture/abc", CancellationToken.None);

        await act.Should().NotThrowAsync();
        result.IsFailure.Should().BeTrue();
    }

    /// <summary>Throws one fixed exception for every request, as a network fault or a client timeout does.</summary>
    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromException<HttpResponseMessage>(exception);
        }
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
