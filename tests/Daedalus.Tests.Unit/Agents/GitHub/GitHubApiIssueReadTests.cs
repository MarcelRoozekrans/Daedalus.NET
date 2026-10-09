using System.Globalization;
using System.Net;
using Daedalus.Infrastructure.Services.GitHub;
using static Daedalus.Tests.Unit.Agents.GitHub.GitHubApiTestSupport;

namespace Daedalus.Tests.Unit.Agents.GitHub;

/// <summary>Phase 2.7: the issue reads behind <c>issues__*</c> and the <c>file-review-findings</c> action.</summary>
public sealed class GitHubApiIssueReadTests
{
    private static readonly RepoRef Repo = RepoRef.Parse("owner/repo").Value;

    /// <summary>Red: read <c>pull_request</c> as absent always; <c>IsPullRequest</c> is then false for #8.</summary>
    [Fact]
    public async Task An_issue_reads_back_its_fields_and_says_when_it_is_a_pull_request()
    {
        var handler = new StubHandler()
            .Route("/issues/7", HttpStatusCode.OK,
                """{"number":7,"title":"A bug","state":"open","body":"text","html_url":"https://github.com/owner/repo/issues/7","labels":[{"name":"bug"}]}""")
            .Route("/issues/8", HttpStatusCode.OK,
                """{"number":8,"title":"A PR","state":"open","body":null,"html_url":"https://github.com/owner/repo/pull/8","labels":[],"pull_request":{}}""");
        var api = Build(handler);

        var issue = (await api.GetIssueAsync(Repo, 7)).Value!;
        var pr = (await api.GetIssueAsync(Repo, 8)).Value!;

        issue.Should().BeEquivalentTo(new IssueDetail(7, "A bug", "open", "text", ["bug"], new Uri("https://github.com/owner/repo/issues/7"), false));
        pr.IsPullRequest.Should().BeTrue();
        pr.Body.Should().BeNull();
    }

    /// <summary>Red: map 404 to a failure like other statuses; the result then fails instead of carrying null.</summary>
    [Fact]
    public async Task A_missing_issue_is_a_successful_null_not_a_failure()
    {
        var result = await Build(new StubHandler()).GetIssueAsync(Repo, 99);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    /// <summary>
    ///     A5. GitHub combines several <c>repo:</c> qualifiers with OR, so a query naming its own would widen the search.
    ///     Red: delete the qualifier check; the request is then sent. Red: drop the double quote from the regex's lead
    ///     class; the quoted row is then sent.
    /// </summary>
    [Theory]
    [InlineData("\"repo:other/x\" crash")]
    [InlineData("crash repo:other/x")]
    [InlineData("org:evil crash")]
    [InlineData("user:someone")]
    [InlineData("OWNER:someone crash")]
    [InlineData("(repo:other/x OR crash)")]
    [InlineData("-repo:other/x crash")]
    public async Task A_query_naming_its_own_scope_is_refused_before_anything_is_sent(string query)
    {
        var handler = new StubHandler();

        var result = await Build(handler).SearchIssuesAsync(Repo, query, IssueSearchState.Open, 10);

        result.IsFailure.Should().BeTrue();
        handler.Requests.Should().BeEmpty();
    }

    /// <summary>
    ///     Red: drop the <c>repository_url</c> filter; the hit from other/x is then returned. Red: drop the
    ///     <c>pull_request</c> filter; #3 is then returned.
    /// </summary>
    [Fact]
    public async Task A_search_is_scoped_to_the_repository_and_returns_issues_only()
    {
        var handler = new StubHandler().Route("/search/issues", HttpStatusCode.OK, """
            {"total_count":3,"items":[
              {"number":1,"title":"Ours","state":"open","html_url":"https://github.com/owner/repo/issues/1","repository_url":"https://api.github.com/repos/owner/repo"},
              {"number":2,"title":"Theirs","state":"open","html_url":"https://github.com/other/x/issues/2","repository_url":"https://api.github.com/repos/other/x"},
              {"number":3,"title":"A PR","state":"open","html_url":"https://github.com/owner/repo/pull/3","repository_url":"https://api.github.com/repos/owner/repo","pull_request":{}}
            ]}
            """);

        var hits = (await Build(handler).SearchIssuesAsync(Repo, "crash", IssueSearchState.Open, 10)).Value;

        hits.Should().ContainSingle().Which.Number.Should().Be(1);
        var q = Uri.UnescapeDataString(handler.Requests.Single().RequestUri!.Query);
        q.Should().Contain("repo:owner/repo is:issue state:open crash").And.Contain("per_page=10");
    }

    /// <summary>Red: stop at the first page always; the 101st issue is then missing.</summary>
    [Fact]
    public async Task Issues_updated_since_are_read_across_pages()
    {
        var page1 = "[" + string.Join(',', Enumerable.Range(1, 100).Select(n =>
            $$"""{"number":{{n}},"body":"b","html_url":"https://github.com/owner/repo/issues/{{n}}","user":{"login":"bot"} }""")) + "]";
        var handler = new StubHandler()
            .Route("&page=1", HttpStatusCode.OK, page1)
            .Route("&page=2", HttpStatusCode.OK, """[{"number":101,"body":"<!-- m -->","html_url":"https://github.com/owner/repo/issues/101","user":{"login":"bot"}}]""");

        var issues = (await Build(handler).ListIssuesUpdatedSinceAsync(Repo, DateTimeOffset.Parse("2026-10-09T10:00:00Z", CultureInfo.InvariantCulture))).Value;

        issues.Should().HaveCount(101);
        handler.Requests[0].RequestUri!.Query.Should().Contain("state=all").And.Contain("since=2026-10-09T10%3A00%3A00Z");
    }

    /// <summary>
    ///     A2: a scan that stopped early would report "not found" for something that exists. Red: change the final return
    ///     of <c>ScanAsync</c> to <c>Success(found)</c>; the 1000 items are then returned.
    /// </summary>
    [Fact]
    public async Task A_scan_longer_than_the_page_cap_fails_rather_than_truncates()
    {
        var fullPage = "[" + string.Join(',', Enumerable.Range(1, 100).Select(n =>
            $$"""{"number":{{n}},"body":"b","html_url":"https://github.com/owner/repo/issues/{{n}}","user":{"login":"bot"} }""")) + "]";
        var handler = new StubHandler().Route("&page=", HttpStatusCode.OK, fullPage);

        var result = await Build(handler).ListIssuesUpdatedSinceAsync(Repo, DateTimeOffset.Parse("2026-10-09T10:00:00Z", CultureInfo.InvariantCulture));

        result.IsFailure.Should().BeTrue();
        handler.Requests.Should().HaveCount(GitHubApi.MaxScanPages);
    }

    /// <summary>Red: treat a 404 page as the end of the scan (return the items so far); the result then succeeds.</summary>
    [Fact]
    public async Task A_page_that_answers_404_mid_scan_is_a_failure_not_a_short_result()
    {
        var fullPage = "[" + string.Join(',', Enumerable.Range(1, 100).Select(n =>
            $$"""{"number":{{n}},"body":"b","html_url":"https://github.com/owner/repo/issues/{{n}}","user":{"login":"bot"} }""")) + "]";
        var handler = new StubHandler().Route("&page=1", HttpStatusCode.OK, fullPage);

        var result = await Build(handler).ListIssuesUpdatedSinceAsync(Repo, DateTimeOffset.Parse("2026-10-09T10:00:00Z", CultureInfo.InvariantCulture));

        result.IsFailure.Should().BeTrue();
    }

    /// <summary>
    ///     Red: drop the <c>number &lt;= 0</c> guard in either method; a request is then sent. Both are guarded.
    /// </summary>
    [Fact]
    public async Task A_non_positive_number_is_refused_before_anything_is_sent()
    {
        var handler = new StubHandler();
        var api = Build(handler);

        var head = await api.GetPullRequestHeadShaAsync(Repo, 0);
        var comments = await api.ListIssueCommentsSinceAsync(Repo, -1, DateTimeOffset.Parse("2026-10-09T10:00:00Z", CultureInfo.InvariantCulture));

        head.IsFailure.Should().BeTrue();
        comments.IsFailure.Should().BeTrue();
        handler.Requests.Should().BeEmpty();
    }

    /// <summary>Red: ignore the state in the qualifier switch; the closed row then lacks <c>state:closed</c>.</summary>
    [Theory]
    [InlineData(IssueSearchState.Closed, "repo:owner/repo is:issue state:closed crash")]
    [InlineData(IssueSearchState.All, "repo:owner/repo is:issue crash")]
    public async Task The_search_state_becomes_its_qualifier(IssueSearchState state, string expected)
    {
        var handler = new StubHandler().Route("/search/issues", HttpStatusCode.OK, """{"items":[]}""");

        await Build(handler).SearchIssuesAsync(Repo, "crash", state, 10);

        Uri.UnescapeDataString(handler.Requests.Single().RequestUri!.Query).Should().Contain($"q={expected}&");
    }

    /// <summary>Red: replace the <c>Math.Clamp</c> with the raw limit; per_page is then 50.</summary>
    [Fact]
    public async Task A_limit_above_the_maximum_is_clamped()
    {
        var handler = new StubHandler().Route("/search/issues", HttpStatusCode.OK, """{"items":[]}""");

        await Build(handler).SearchIssuesAsync(Repo, "crash", IssueSearchState.Open, 50);

        handler.Requests.Single().RequestUri!.Query.Should().Contain("per_page=10").And.NotContain("per_page=50");
    }

    /// <summary>Red: read <c>base.sha</c> instead; the result is then the base commit.</summary>
    [Fact]
    public async Task The_pull_request_head_sha_is_read()
    {
        var handler = new StubHandler().Route("/pulls/7", HttpStatusCode.OK, """{"head":{"sha":"abc123"},"base":{"sha":"def456"}}""");

        (await Build(handler).GetPullRequestHeadShaAsync(Repo, 7)).Value.Should().Be("abc123");
    }

    /// <summary>Red: return the comments of the first page without a since filter; the query assertion fails.</summary>
    [Fact]
    public async Task Comments_since_are_read_with_the_since_filter()
    {
        var handler = new StubHandler().Route("/issues/7/comments", HttpStatusCode.OK,
            """[{"id":5,"body":"x","html_url":"https://github.com/owner/repo/issues/7#issuecomment-5","user":{"login":"bot"}}]""");

        var comments = (await Build(handler).ListIssueCommentsSinceAsync(Repo, 7, DateTimeOffset.Parse("2026-10-09T10:00:00Z", CultureInfo.InvariantCulture))).Value;

        comments.Should().ContainSingle().Which.Id.Should().Be(5);
        handler.Requests.Single().RequestUri!.Query.Should().Contain("since=");
    }

    /// <summary>Red: let a <c>JsonException</c> escape; this test then throws.</summary>
    [Fact]
    public async Task A_body_that_is_not_json_is_a_failure_not_an_exception()
    {
        var handler = new StubHandler().Route("/pulls/7", () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>proxy</html>"),
        });

        (await Build(handler).GetPullRequestHeadShaAsync(Repo, 7)).IsFailure.Should().BeTrue();
    }

    /// <summary>Red: read <c>name</c> instead of <c>login</c>; the property is then missing and the read fails.</summary>
    [Fact]
    public async Task The_authenticated_login_is_read()
    {
        var handler = new StubHandler().Route("/user", HttpStatusCode.OK, """{"login":"daedalus-bot","name":"Not This"}""");

        (await Build(handler).GetAuthenticatedLoginAsync()).Value.Should().Be("daedalus-bot");
    }

    /// <summary>Red: treat a 404 as an empty login; the result then succeeds.</summary>
    [Fact]
    public async Task A_missing_authenticated_account_is_a_failure()
    {
        (await Build(new StubHandler()).GetAuthenticatedLoginAsync()).IsFailure.Should().BeTrue();
    }

    /// <summary>The author decides whether a marker is Daedalus's own. Red: pass a constant instead of <c>user.login</c>; the logins differ.</summary>
    [Fact]
    public async Task The_author_of_each_issue_and_comment_is_read()
    {
        var handler = new StubHandler()
            .Route("/issues?", HttpStatusCode.OK, """[{"number":1,"body":"b","html_url":"https://github.com/owner/repo/issues/1","user":{"login":"alice"}}]""")
            .Route("/issues/7/comments", HttpStatusCode.OK, """[{"id":5,"body":"x","html_url":"https://github.com/owner/repo/issues/7#issuecomment-5","user":{"login":"Bob"}}]""");
        var api = Build(handler);
        var since = DateTimeOffset.Parse("2026-10-09T10:00:00Z", CultureInfo.InvariantCulture);

        (await api.ListIssuesUpdatedSinceAsync(Repo, since)).Value.Should().ContainSingle().Which.AuthorLogin.Should().Be("alice");
        (await api.ListIssueCommentsSinceAsync(Repo, 7, since)).Value.Should().ContainSingle().Which.AuthorLogin.Should().Be("Bob");
    }

    /// <summary>A deleted account has a null user. Red: default the login to empty; the scans then succeed.</summary>
    [Fact]
    public async Task An_item_with_no_author_is_a_shape_failure()
    {
        var handler = new StubHandler()
            .Route("/issues?", HttpStatusCode.OK, """[{"number":1,"body":"b","html_url":"https://github.com/owner/repo/issues/1","user":null}]""")
            .Route("/issues/7/comments", HttpStatusCode.OK, """[{"id":5,"body":"x","html_url":"https://github.com/owner/repo/issues/7#issuecomment-5"}]""");
        var api = Build(handler);
        var since = DateTimeOffset.Parse("2026-10-09T10:00:00Z", CultureInfo.InvariantCulture);

        (await api.ListIssuesUpdatedSinceAsync(Repo, since)).IsFailure.Should().BeTrue();
        (await api.ListIssueCommentsSinceAsync(Repo, 7, since)).IsFailure.Should().BeTrue();
    }
}
