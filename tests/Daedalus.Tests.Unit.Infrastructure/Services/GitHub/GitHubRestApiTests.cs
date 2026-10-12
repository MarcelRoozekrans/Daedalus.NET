using System.Net;
using System.Text;
using System.Text.Json;
using Daedalus.Infrastructure.Services.GitHub;
using ZeroAlloc.Rest.SystemTextJson;

namespace Daedalus.Tests.Unit.Infrastructure.Services.GitHub;

/// <summary>Phase 2.11: the generated client sends what GitHub expects and reads what GitHub answers.</summary>
public sealed class GitHubRestApiTests
{
    /// <summary>A trimmed issue listing in GitHub's own shape: more fields than Daedalus reads, an issue and a pull request.</summary>
    private const string RecordedIssues = """
        [
          {"url":"https://api.github.com/repos/octo/repo/issues/1","repository_url":"https://api.github.com/repos/octo/repo",
           "html_url":"https://github.com/octo/repo/issues/1","id":1001,"node_id":"I_1","number":1,"title":"Crash on start",
           "user":{"login":"octocat","id":1,"type":"User"},"labels":[{"id":7,"name":"bug","color":"d73a4a","default":true}],
           "state":"open","locked":false,"assignee":null,"comments":2,"created_at":"2026-10-08T09:00:00Z",
           "updated_at":"2026-10-09T11:00:00Z","closed_at":null,"author_association":"OWNER","body":"It crashes.",
           "reactions":{"total_count":0}},
          {"url":"https://api.github.com/repos/octo/repo/issues/2","repository_url":"https://api.github.com/repos/octo/repo",
           "html_url":"https://github.com/octo/repo/pull/2","id":1002,"number":2,"title":"Fix crash","user":{"login":"hubot"},
           "labels":[],"state":"open","updated_at":"2026-10-09T12:00:00Z","body":null,
           "pull_request":{"url":"https://api.github.com/repos/octo/repo/pulls/2","html_url":"https://github.com/octo/repo/pull/2","merged_at":null}}
        ]
        """;

    /// <summary>
    ///     Red: give <c>GetRepositoryAsync</c> the route <c>/repos/{owner}/{repo}</c>, with a leading slash; the base path
    ///     <c>/api/v3</c> is then dropped.
    /// </summary>
    [Fact]
    public async Task A_route_is_escaped_and_relative_to_the_base_address()
    {
        var handler = RecordingHandler.Answering(_ => Json("""{"default_branch":"trunk"}"""));

        await Client(handler, "https://ghe.example/api/v3/").GetRepositoryAsync("o w", "r", "Bearer t", "Daedalus", CancellationToken.None);

        handler.Sent[0].Uri.AbsoluteUri.Should().Be("https://ghe.example/api/v3/repos/o%20w/r");
    }

    /// <summary>
    ///     Red: delete the static <c>X-GitHub-Api-Version</c> header from <c>GetIssueAsync</c>; the version assertion fails.
    ///     Red: delete its static <c>Accept</c> header; the media-type assertion fails.
    ///     Red: name the token's header <c>X-Authorization</c>; the authorization assertion fails.
    ///     Red: name the user agent's header <c>X-User-Agent</c>; the user-agent assertion fails.
    /// </summary>
    [Fact]
    public async Task Static_and_per_call_headers_are_sent()
    {
        var handler = RecordingHandler.Answering(_ => Json("""{"number":7}"""));

        await Client(handler).GetIssueAsync("o", "r", 7, "Bearer t", "Daedalus", CancellationToken.None);

        var headers = handler.Sent[0].Headers;
        headers["X-GitHub-Api-Version"].Should().Be("2022-11-28");
        headers["Accept"].Should().Contain("application/vnd.github+json");
        headers["Authorization"].Should().Be("Bearer t");
        headers["User-Agent"].Should().Be("Daedalus");
    }

    /// <summary>Red: declare <c>perPage</c> before <c>state</c> in <c>ListPullRequestsAsync</c>; the query order changes.</summary>
    [Fact]
    public async Task A_null_query_value_is_left_out_and_the_rest_keep_todays_order()
    {
        var handler = RecordingHandler.Answering(_ => Json("[]"));

        await Client(handler).ListPullRequestsAsync("o", "r", "open", null, null, null, 15, "Bearer t", "Daedalus", CancellationToken.None);

        handler.Sent[0].Uri.Query.Should().Be("?state=open&per_page=15");
    }

    /// <summary>
    ///     How an absent <c>pull_request</c> reads is pinned at the facade, by Task 2's
    ///     <c>A_pull_request_is_told_by_the_presence_of_its_marker</c>.
    ///     Red: drop <c>PropertyNamingPolicy = SnakeCaseLower</c> from <c>GitHubJsonContext</c>; <c>html_url</c> no longer
    ///     binds, and the link assertion fails.
    ///     Red: bind <c>GitHubUserDto.Login</c> to the JSON name <c>name</c>; the login assertion fails.
    ///     Red: bind <c>GitHubLabelDto.Name</c> to the JSON name <c>label</c>; the label assertion fails.
    ///     Red: bind <c>GitHubIssueDto.PullRequest</c> to the JSON name <c>pull_request_marker</c>; nothing binds it, and the marker
    ///     assertion on #2 fails.
    /// </summary>
    [Fact]
    public async Task A_recorded_issue_listing_binds_its_snake_case_fields()
    {
        var handler = RecordingHandler.Answering(_ => Json(RecordedIssues));

        var listed = await Client(handler).ListIssuesAsync("octo", "repo", "2026-10-09T10:00:00Z", "all", 100, 1, "Bearer t", "Daedalus", CancellationToken.None);

        var issues = listed.Value;
        issues[0].HtmlUrl.Should().Be("https://github.com/octo/repo/issues/1");
        issues[0].User!.Login.Should().Be("octocat");
        issues[0].Labels![0].Name.Should().Be("bug");
        issues[1].PullRequest.ValueKind.Should().Be(JsonValueKind.Object);
    }

    /// <summary>
    ///     Red: rename <c>GitHubCreatePullRequestBody.Base</c> to <c>BaseBranch</c>; <c>base_branch</c> is then sent.
    /// </summary>
    [Fact]
    public async Task A_pull_request_body_is_sent_in_GitHubs_field_names()
    {
        var handler = RecordingHandler.Answering(_ => Json("""{"number":42,"url":"u","html_url":"h"}""", HttpStatusCode.Created));

        await Client(handler).CreatePullRequestAsync(
            "o", "r", new GitHubCreatePullRequestBody("a title", "a description", "feature", "main", Draft: false), "Bearer t", "Daedalus", CancellationToken.None);

        JsonDocument.Parse(handler.Sent[0].Body!).RootElement.GetProperty("base").GetString().Should().Be("main");
    }

    /// <summary>
    ///     Red: map the fall-through branch of <c>GitHubErrorMapper.Map</c>, the deserialization failure, to Server; the kind assertion fails.
    ///     Red: drop the status from that branch; the status assertion fails.
    /// </summary>
    [Fact]
    public async Task A_body_that_is_not_json_is_unreadable_with_its_status()
    {
        var handler = RecordingHandler.Answering(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>proxy</html>") });

        var read = await Client(handler).GetPullRequestAsync("o", "r", 7, "Bearer t", "Daedalus", CancellationToken.None);

        read.Error.Kind.Should().Be(GitHubErrorKind.Unreadable);
        read.Error.StatusCode.Should().Be(200);
    }

    /// <summary>
    ///     Red: give <c>CreateCommentAsync</c> the return type <c>Result&lt;GitHubIssueDto, GitHubError&gt;</c>; a 201 with an
    ///     empty body is then a deserialization failure.
    /// </summary>
    [Fact]
    public async Task A_write_without_a_result_succeeds_on_any_2xx_without_reading_the_body()
    {
        var handler = RecordingHandler.Answering(_ => new HttpResponseMessage(HttpStatusCode.Created));

        var posted = await Client(handler).CreateCommentAsync("o", "r", 7, new GitHubCommentBody("a note"), "Bearer t", "Daedalus", CancellationToken.None);

        posted.IsSuccess.Should().BeTrue();
    }

    private static GitHubRestApiClient Client(RecordingHandler handler, string baseAddress = "https://api.github.com/") =>
        new GitHubRestApiClient(
            new HttpClient(handler) { BaseAddress = new Uri(baseAddress) },
            new SystemTextJsonSerializer(GitHubJsonContext.Default),
            new GitHubErrorMapper(TimeProvider.System));

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
