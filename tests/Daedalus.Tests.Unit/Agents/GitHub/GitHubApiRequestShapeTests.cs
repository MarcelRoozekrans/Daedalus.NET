using System.Globalization;
using System.Net;
using Daedalus.Infrastructure.Services.GitHub;
using static Daedalus.Tests.Unit.Agents.GitHub.GitHubApiTestSupport;

namespace Daedalus.Tests.Unit.Agents.GitHub;

/// <summary>
///     Phase 2.11 characterisation: what <see cref="GitHubApi"/> sends for every endpoint, pinned before its transport
///     moves to ZeroAlloc.Rest. These assertions run unchanged against the rebuilt facade; only <see cref="Outcome{T}"/>
///     changes its parameter type.
/// </summary>
public sealed class GitHubApiRequestShapeTests
{
    private static readonly RepoRef Repo = RepoRef.Parse("owner/repo").Value;
    private static readonly DateTimeOffset Since = DateTimeOffset.Parse("2026-10-09T10:00:00Z", CultureInfo.InvariantCulture);
    private static readonly DateTime ActivitySince = GitHubApiTestSupport.Now.AddHours(-24);

    public static TheoryData<string, string, string, string, string?> Requests => new()
    {
        { "default-branch", "GET", "/repos/owner/repo", "", null },
        { "issue", "GET", "/repos/owner/repo/issues/7", "", null },
        { "search", "GET", "/search/issues", "per_page=10&q=repo:owner/repo is:issue state:open crash", null },
        { "issues-since", "GET", "/repos/owner/repo/issues", "page=1&per_page=100&since=2026-10-09T10:00:00Z&state=all", null },
        { "comments-since", "GET", "/repos/owner/repo/issues/7/comments", "page=1&per_page=100&since=2026-10-09T10:00:00Z", null },
        { "pull-request-head", "GET", "/repos/owner/repo/pulls/7", "", null },
        { "login", "GET", "/user", "", null },
        { "find-open-pull-request", "GET", "/repos/owner/repo/pulls", "head=owner:manufacture/abc&state=open", null },
        { "comment", "POST", "/repos/owner/repo/issues/7/comments", "", """{"body":"a note"}""" },
        { "label", "POST", "/repos/owner/repo/issues/7/labels", "", """{"labels":["stale"]}""" },
        { "close", "PATCH", "/repos/owner/repo/issues/7", "", """{"state":"closed"}""" },
        { "create-issue", "POST", "/repos/owner/repo/issues", "", """{"title":"A title","body":"A body"}""" },
        { "create-pull-request", "POST", "/repos/owner/repo/pulls", "", """{"title":"a title","body":"a description","head":"feature","base":"main","draft":false}""" },
    };

    public static TheoryData<string> Endpoints => new()
    {
        "default-branch", "issue", "search", "issues-since", "comments-since", "pull-request-head", "login",
        "find-open-pull-request", "comment", "label", "close", "create-issue", "create-pull-request",
    };

    /// <summary>
    ///     Every endpoint is answered 404, so what is checked is only what was sent.
    ///     Red: send <c>CloseIssueAsync</c> as a POST; the close row fails on the method.
    ///     Red: build <c>RepoUrl</c> on another host; every repository row fails on the host.
    ///     Red: drop <c>/comments</c> from <c>CommentAsync</c>'s URL; the comment row fails on the path.
    ///     Red: drop <c>&amp;per_page=100</c> from the issue scan's URL; the issues-since row fails on the query.
    ///     Red: rename <c>body</c> to <c>text</c> in <c>CommentAsync</c>'s payload; the comment row fails on the body.
    ///     Red: give the repository read a JSON body; the default-branch row fails on the null-body check.
    /// </summary>
    [Theory]
    [MemberData(nameof(Requests))]
    public async Task Each_endpoint_sends_its_method_path_query_and_body(string endpoint, string method, string path, string query, string? body)
    {
        var handler = new StubHandler();

        await CallAsync(Build(handler), endpoint);

        var sent = handler.Requests[0];
        sent.Method.Method.Should().Be(method);
        sent.RequestUri!.Host.Should().Be("api.github.com");
        sent.RequestUri.AbsolutePath.Should().Be(path);
        CanonicalQuery(sent).Should().Be(query);
        if (body is null)
            handler.Bodies[0].Should().BeNull();
        else
            ShouldBeJson(handler.Bodies[0], body);
    }

    /// <summary>
    ///     Red: drop the Authorization line from <c>ApplyHeaders</c>; the scheme assertion throws on a null header.
    ///     Red: send the token with the scheme <c>token</c>; the scheme assertion fails.
    ///     Red: send a constant instead of the token; the parameter assertion fails.
    ///     Red: drop the User-Agent line; the user-agent assertion fails.
    ///     Red: send API version <c>2026-03-10</c>; the version assertion fails.
    ///     Red: send <c>Accept: application/json</c> only; the media-type assertion fails.
    /// </summary>
    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Every_request_carries_the_token_the_user_agent_the_api_version_and_the_media_type(string endpoint)
    {
        var handler = new StubHandler();

        await CallAsync(Build(handler), endpoint);

        var sent = handler.Requests[0];
        sent.Headers.Authorization!.Scheme.Should().Be("Bearer");
        sent.Headers.Authorization.Parameter.Should().Be("ghp_example");
        sent.Headers.UserAgent.ToString().Should().Contain("Daedalus");
        sent.Headers.GetValues("X-GitHub-Api-Version").Should().Equal("2022-11-28");
        sent.Headers.GetValues("Accept").Should().Contain(value => value.Contains("application/vnd.github+json", StringComparison.Ordinal));
    }

    /// <summary>
    ///     The digest's six reads, in order, with their queries.
    ///     Red: read the open pull requests before the merged ones; the order fails.
    ///     Red: drop <c>&amp;sort=updated</c> from the merged read; its entry fails.
    ///     Red: read the commits of <c>main</c> instead of the reported default branch; the commits entry fails.
    /// </summary>
    [Fact]
    public async Task The_activity_read_sends_six_requests_in_order()
    {
        var handler = WithActivityRoutes(new StubHandler());

        await Build(handler).GetActivityAsync(Repo, ActivitySince);

        handler.Requests.Select(r => $"{r.RequestUri!.AbsolutePath}?{CanonicalQuery(r)}").Should().Equal(
            "/repos/owner/repo?",
            "/repos/owner/repo/commits?per_page=15&sha=trunk&since=2026-09-18T07:00:00Z",
            "/repos/owner/repo/pulls?direction=desc&per_page=15&sort=updated&state=closed",
            "/repos/owner/repo/pulls?per_page=15&state=open",
            "/repos/owner/repo/issues?per_page=15&since=2026-09-18T07:00:00Z&state=all",
            "/repos/owner/repo/actions/runs?per_page=15&status=failure");
    }

    /// <summary>Red: in <c>RunCategoryAsync</c>, rethrow instead of returning <c>Failed</c>; the call throws.</summary>
    [Theory]
    [InlineData("/commits", "commits")]
    [InlineData("pulls?state=closed", "merged")]
    [InlineData("pulls?state=open", "open")]
    [InlineData("/issues?", "issues")]
    [InlineData("/actions/runs", "runs")]
    public async Task A_category_that_fails_takes_no_other_category_down(string failing, string category)
    {
        var handler = WithActivityRoutes(new StubHandler().Route(failing, HttpStatusCode.InternalServerError, """{"message":"boom"}"""));

        var activity = await Build(handler).GetActivityAsync(Repo, ActivitySince);

        Unchecked(activity).Should().Equal(category);
    }

    /// <summary>
    ///     Red: fall back to <c>main</c> when no default branch is reported; the commits category is then checked.
    ///     Red: fail the category with an empty reason; the reason assertion fails.
    /// </summary>
    [Fact]
    public async Task A_repository_without_a_default_branch_fails_only_the_commits()
    {
        var handler = WithActivityRoutes(new StubHandler(), defaultBranch: "{}");

        var activity = await Build(handler).GetActivityAsync(Repo, ActivitySince);

        Unchecked(activity).Should().Equal("commits");
        activity.Commits.Unavailable.Should().Contain("default branch");
    }

    /// <summary>
    ///     Red: skip <c>AddRange</c> for a full page; the count is 1.
    ///     Red: drop the short-page stop in <c>ScanAsync</c>; page 3 is then requested, and the page list fails.
    /// </summary>
    [Fact]
    public async Task An_issue_scan_stops_at_the_first_short_page()
    {
        var handler = new StubHandler()
            .Route("&page=1", HttpStatusCode.OK, Issues(1, 100))
            .Route("&page=2", HttpStatusCode.OK, Issues(101, 1));

        var issues = await Build(handler).ListIssuesUpdatedSinceAsync(Repo, Since);

        issues.Value.Should().HaveCount(101);
        handler.Requests.Select(r => QueryOf(r)["page"]).Should().Equal("1", "2");
    }

    /// <summary>
    ///     Red: skip <c>AddRange</c> for a full page; the count is 0.
    ///     Red: stop after the first page whatever its size; the page list is then "1" alone.
    /// </summary>
    [Fact]
    public async Task A_comment_scan_reads_every_full_page_and_stops_at_an_empty_one()
    {
        var handler = new StubHandler()
            .Route("&page=1", HttpStatusCode.OK, Comments(100))
            .Route("&page=2", HttpStatusCode.OK, "[]");

        var comments = await Build(handler).ListIssueCommentsSinceAsync(Repo, 7, Since);

        comments.Value.Should().HaveCount(100);
        handler.Requests.Select(r => QueryOf(r)["page"]).Should().Equal("1", "2");
    }

    /// <summary>
    ///     <c>pull_request</c> marks a pull request whether it is an object or <c>null</c>; only its absence marks an issue.
    ///     Red: test <c>pull_request</c> for an object value instead of its presence; the null row then reads as an issue.
    /// </summary>
    [Theory]
    [InlineData("""{"number":7,"title":"t","state":"open","html_url":"https://github.com/owner/repo/pull/7","pull_request":{}}""", true)]
    [InlineData("""{"number":7,"title":"t","state":"open","html_url":"https://github.com/owner/repo/pull/7","pull_request":null}""", true)]
    [InlineData("""{"number":7,"title":"t","state":"open","html_url":"https://github.com/owner/repo/issues/7"}""", false)]
    public async Task A_pull_request_is_told_by_the_presence_of_its_marker(string json, bool isPullRequest)
    {
        var handler = new StubHandler().Route("/issues/7", HttpStatusCode.OK, json);

        var issue = await Build(handler).GetIssueAsync(Repo, 7);

        issue.Value!.IsPullRequest.Should().Be(isPullRequest);
    }

    /// <summary>
    ///     404 answers "no such issue" only for an issue read; everywhere else it is a failure.
    ///     Red: map 404 to a failure in the issue read; the issue row fails on <c>isSuccess</c>.
    ///     Red: in <c>GetPullRequestHeadShaAsync</c>, return <c>Success("")</c> when the document is null; the
    ///     pull-request-head row fails on <c>isSuccess</c>.
    ///     Red: return an empty issue instead of null on 404; the issue row fails on the value.
    ///     Red: in <c>ScanAsync</c>, return <c>Success(found)</c> when the document is null; the issues-since and
    ///     comments-since rows fail on <c>isSuccess</c>.
    ///     Red: in <c>SearchIssuesAsync</c>, return an empty list when the document is null; the search row fails
    ///     on <c>isSuccess</c>.
    ///     Red: in <c>GetAuthenticatedLoginAsync</c>, return <c>Success("")</c> when the document is null; the login
    ///     row fails on <c>isSuccess</c>.
    /// </summary>
    [Theory]
    [InlineData("issue", true)]
    [InlineData("default-branch", false)]
    [InlineData("search", false)]
    [InlineData("issues-since", false)]
    [InlineData("comments-since", false)]
    [InlineData("pull-request-head", false)]
    [InlineData("login", false)]
    [InlineData("find-open-pull-request", false)]
    [InlineData("comment", false)]
    [InlineData("label", false)]
    [InlineData("close", false)]
    [InlineData("create-issue", false)]
    [InlineData("create-pull-request", false)]
    public async Task A_404_is_a_null_success_only_for_an_issue_read(string endpoint, bool succeeds)
    {
        var (isSuccess, value) = await CallAsync(Build(new StubHandler()), endpoint);

        isSuccess.Should().Be(succeeds);
        if (succeeds)
            value.Should().BeNull();
    }

    /// <summary>Calls one endpoint, so one theory covers every method whatever its result type.</summary>
    private static async Task<(bool IsSuccess, object? Value)> CallAsync(GitHubApi api, string endpoint) => endpoint switch
    {
        "default-branch" => Outcome(await api.GetDefaultBranchAsync(Repo)),
        "issue" => Outcome(await api.GetIssueAsync(Repo, 7)),
        "search" => Outcome(await api.SearchIssuesAsync(Repo, "crash", IssueSearchState.Open, 10)),
        "issues-since" => Outcome(await api.ListIssuesUpdatedSinceAsync(Repo, Since)),
        "comments-since" => Outcome(await api.ListIssueCommentsSinceAsync(Repo, 7, Since)),
        "pull-request-head" => Outcome(await api.GetPullRequestHeadShaAsync(Repo, 7)),
        "login" => Outcome(await api.GetAuthenticatedLoginAsync()),
        "find-open-pull-request" => Outcome(await api.FindOpenPullRequestAsync(Repo, "manufacture/abc")),
        "comment" => Outcome(await api.CommentAsync(Repo, 7, "a note")),
        "label" => Outcome(await api.AddLabelAsync(Repo, 7, "stale")),
        "close" => Outcome(await api.CloseIssueAsync(Repo, 7)),
        "create-issue" => Outcome(await api.CreateIssueAsync(Repo, "A title", "A body")),
        "create-pull-request" => Outcome(await api.CreatePullRequestAsync(Repo, "feature", "main", "a title", "a description")),
        _ => throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, "not an endpoint of this test"),
    };

    /// <summary>The one place that knows the facade's result type; Task 5 retypes it.</summary>
    private static (bool IsSuccess, object? Value) Outcome<T>(Result<T> result) =>
        (result.IsSuccess, result.IsSuccess ? result.Value : null);

    private static StubHandler WithActivityRoutes(StubHandler handler, string defaultBranch = """{"default_branch":"trunk"}""") =>
        handler
            .Route("/repos/owner/repo/commits", HttpStatusCode.OK, "[]")
            .Route("/repos/owner/repo/actions/runs", HttpStatusCode.OK, """{"workflow_runs":[]}""")
            .Route("/repos/owner/repo/pulls", HttpStatusCode.OK, "[]")
            .Route("/repos/owner/repo/issues", HttpStatusCode.OK, "[]")
            .Route("/repos/owner/repo", HttpStatusCode.OK, defaultBranch);

    private static string[] Unchecked(RepoActivity activity) =>
    [
        .. new (string Name, bool Checked)[]
            {
                ("commits", activity.Commits.Checked),
                ("merged", activity.MergedPullRequests.Checked),
                ("open", activity.OpenPullRequests.Checked),
                ("issues", activity.Issues.Checked),
                ("runs", activity.FailedRuns.Checked),
            }
            .Where(category => !category.Checked)
            .Select(category => category.Name),
    ];

    private static string Issues(int first, int count) =>
        "[" + string.Join(',', Enumerable.Range(first, count).Select(n =>
            $$"""{"number":{{n}},"body":"b","html_url":"https://github.com/owner/repo/issues/{{n}}","user":{"login":"bot"} }""")) + "]";

    private static string Comments(int count) =>
        "[" + string.Join(',', Enumerable.Range(1, count).Select(n =>
            $$"""{"id":{{n}},"body":"c","html_url":"https://github.com/owner/repo/issues/7#issuecomment-{{n}}","user":{"login":"bot"} }""")) + "]";
}
