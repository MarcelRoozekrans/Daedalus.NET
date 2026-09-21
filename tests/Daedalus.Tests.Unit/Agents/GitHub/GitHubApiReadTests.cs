using System.Globalization;
using System.Net;
using Daedalus.Infrastructure.Services.GitHub;
using static Daedalus.Tests.Unit.Agents.GitHub.GitHubApiTestSupport;

namespace Daedalus.Tests.Unit.Agents.GitHub;

public class GitHubApiReadTests
{
    private static readonly DateTime Now = GitHubApiTestSupport.Now;
    private static readonly DateTime Since = Now.AddHours(-24);

    [Fact]
    public async Task The_request_carries_the_auth_header_the_user_agent_and_the_api_version()
    {
        var handler = new StubHandler().Route("/repos/owner/repo", HttpStatusCode.OK, """{"default_branch":"trunk"}""");

        await Build(handler).GetDefaultBranchAsync(RepoRef.Parse("owner/repo").Value);

        var sent = handler.Requests.Should().ContainSingle().Subject;
        sent.RequestUri!.ToString().Should().Be("https://api.github.com/repos/owner/repo");
        sent.Headers.Authorization!.Scheme.Should().Be("Bearer");
        sent.Headers.Authorization.Parameter.Should().Be("ghp_example");
        sent.Headers.UserAgent.ToString().Should().Contain("Daedalus", "GitHub rejects requests without a User-Agent");
        sent.Headers.Should().ContainKey("X-GitHub-Api-Version");
    }

    [Fact]
    public async Task The_default_branch_is_read_from_the_repository_not_assumed()
    {
        var handler = new StubHandler().Route("/repos/owner/repo", HttpStatusCode.OK, """{"default_branch":"trunk"}""");

        var branch = await Build(handler).GetDefaultBranchAsync(RepoRef.Parse("owner/repo").Value);

        branch.Value.Should().Be("trunk", "a caller may name any repository, and not every repository uses main");
    }

    [Fact]
    public async Task A_category_that_fails_is_reported_unchecked_and_the_others_still_report()
    {
        var handler = new StubHandler()
            .Route("/repos/owner/repo/commits", HttpStatusCode.OK,
                """[{"sha":"abc1234","commit":{"message":"a change","author":{"name":"someone","date":"2026-09-19T06:00:00Z"}}}]""")
            .Route("/repos/owner/repo/actions/runs", HttpStatusCode.InternalServerError, """{"message":"boom"}""")
            .Route("/repos/owner/repo/pulls", HttpStatusCode.OK, "[]")
            .Route("/repos/owner/repo/issues", HttpStatusCode.OK, "[]")
            .Route("/repos/owner/repo", HttpStatusCode.OK, """{"default_branch":"main"}""");

        var activity = await Build(handler).GetActivityAsync(RepoRef.Parse("owner/repo").Value, Since);

        activity.Commits.Checked.Should().BeTrue();
        activity.Commits.Items.Should().ContainSingle();
        activity.FailedRuns.Checked.Should().BeFalse("the CI read failed, so we must not claim CI was clean");
        activity.FailedRuns.Unavailable.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_404_says_both_things_it_could_mean()
    {
        var handler = new StubHandler().Route("/repos/owner/repo", HttpStatusCode.NotFound, """{"message":"Not Found"}""");

        var result = await Build(handler).GetDefaultBranchAsync(RepoRef.Parse("owner/repo").Value);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().ContainEquivalentOf("does not exist");
        result.Error.Should().ContainEquivalentOf("token",
            "from outside, a missing repository and one the token cannot see are identical");
    }

    [Fact]
    public async Task A_rate_limited_403_is_distinguished_from_a_forbidden_403_and_says_when_it_resets()
    {
        var reset = DateTimeOffset.FromUnixTimeSeconds(1789000000);
        var handler = new StubHandler().Route("/repos/owner/repo", () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("""{"message":"rate limit exceeded"}""",
                    System.Text.Encoding.UTF8, "application/json"),
            };
            response.Headers.Add("X-RateLimit-Remaining", "0");
            response.Headers.Add("X-RateLimit-Reset", reset.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
            return response;
        });

        var result = await Build(handler).GetDefaultBranchAsync(RepoRef.Parse("owner/repo").Value);

        result.Error.Should().ContainEquivalentOf("rate limit");
        result.Error.Should().Contain(reset.UtcDateTime.ToString("u", CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task A_forbidden_403_that_is_not_rate_limiting_says_so_instead()
    {
        var handler = new StubHandler().Route("/repos/owner/repo", () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("""{"message":"Resource not accessible"}""",
                    System.Text.Encoding.UTF8, "application/json"),
            };
            response.Headers.Add("X-RateLimit-Remaining", "4999");
            return response;
        });

        var result = await Build(handler).GetDefaultBranchAsync(RepoRef.Parse("owner/repo").Value);

        result.Error.Should().NotContainEquivalentOf("rate limit");
    }

    [Fact]
    public async Task A_missing_token_fails_before_any_request_is_sent()
    {
        var handler = new StubHandler();

        var result = await Build(handler, token: null).GetDefaultBranchAsync(RepoRef.Parse("owner/repo").Value);

        result.IsFailure.Should().BeTrue();
        handler.Requests.Should().BeEmpty("an unauthenticated request would 404 and look like a missing repository");
    }

    [Fact]
    public async Task A_category_at_the_ceiling_reports_that_it_was_truncated()
    {
        var many = string.Join(",", Enumerable.Range(0, 50).Select(i =>
            $"{{\"sha\":\"sha{i}\",\"commit\":{{\"message\":\"m{i}\",\"author\":{{\"name\":\"a\",\"date\":\"2026-09-19T06:00:00Z\"}}}}}}"));

        var handler = new StubHandler()
            .Route("/repos/owner/repo/commits", HttpStatusCode.OK, $"[{many}]")
            .Route("/repos/owner/repo/actions/runs", HttpStatusCode.OK, """{"workflow_runs":[]}""")
            .Route("/repos/owner/repo/pulls", HttpStatusCode.OK, "[]")
            .Route("/repos/owner/repo/issues", HttpStatusCode.OK, "[]")
            .Route("/repos/owner/repo", HttpStatusCode.OK, """{"default_branch":"main"}""");

        var activity = await Build(handler).GetActivityAsync(RepoRef.Parse("owner/repo").Value, Since);

        activity.Commits.Truncated.Should().BeTrue("a silent cap reads as complete coverage");
    }

    [Fact]
    public async Task The_window_it_covered_is_reported_back()
    {
        var handler = new StubHandler()
            .Route("/repos/owner/repo/commits", HttpStatusCode.OK, "[]")
            .Route("/repos/owner/repo/actions/runs", HttpStatusCode.OK, """{"workflow_runs":[]}""")
            .Route("/repos/owner/repo/pulls", HttpStatusCode.OK, "[]")
            .Route("/repos/owner/repo/issues", HttpStatusCode.OK, "[]")
            .Route("/repos/owner/repo", HttpStatusCode.OK, """{"default_branch":"main"}""");

        var activity = await Build(handler).GetActivityAsync(RepoRef.Parse("owner/repo").Value, Since);

        activity.WindowStartUtc.Should().Be(Since);
        activity.WindowEndUtc.Should().Be(Now, "the end of the window is the injected clock, never DateTime.UtcNow");
    }

    [Fact]
    public async Task Truncation_of_merged_pull_requests_is_measured_before_filtering_by_merge_date()
    {
        var unmergedOrOld = string.Join(",", Enumerable.Range(0, 45).Select(i =>
            $"{{\"number\":{i},\"title\":\"pr{i}\",\"user\":{{\"login\":\"a\"}},\"updated_at\":\"2026-09-19T06:00:00Z\",\"merged_at\":null}}"));
        var recentlyMerged = string.Join(",", Enumerable.Range(45, 5).Select(i =>
            $"{{\"number\":{i},\"title\":\"pr{i}\",\"user\":{{\"login\":\"a\"}},\"updated_at\":\"2026-09-19T06:00:00Z\",\"merged_at\":\"2026-09-19T06:00:00Z\"}}"));

        var handler = new StubHandler()
            .Route("/repos/owner/repo/commits", HttpStatusCode.OK, "[]")
            .Route("/repos/owner/repo/actions/runs", HttpStatusCode.OK, """{"workflow_runs":[]}""")
            .Route("/repos/owner/repo/pulls", HttpStatusCode.OK, $"[{unmergedOrOld},{recentlyMerged}]")
            .Route("/repos/owner/repo/issues", HttpStatusCode.OK, "[]")
            .Route("/repos/owner/repo", HttpStatusCode.OK, """{"default_branch":"main"}""");

        var activity = await Build(handler).GetActivityAsync(RepoRef.Parse("owner/repo").Value, Since);

        activity.MergedPullRequests.Items.Should().HaveCount(5,
            "only five of the fifty raw results are merged within the window");
        activity.MergedPullRequests.Truncated.Should().BeTrue(
            "the raw page fetched was a full page of fifty, even though filtering by merge date left only five");
    }

    [Fact]
    public async Task Truncation_of_issues_is_measured_before_dropping_pull_requests()
    {
        var pullRequestsFromIssuesEndpoint = string.Join(",", Enumerable.Range(0, 45).Select(i =>
            $"{{\"number\":{i},\"title\":\"pr{i}\",\"state\":\"open\",\"updated_at\":\"2026-09-19T06:00:00Z\",\"pull_request\":{{}}}}"));
        var actualIssues = string.Join(",", Enumerable.Range(45, 5).Select(i =>
            $"{{\"number\":{i},\"title\":\"issue{i}\",\"state\":\"open\",\"updated_at\":\"2026-09-19T06:00:00Z\"}}"));

        var handler = new StubHandler()
            .Route("/repos/owner/repo/commits", HttpStatusCode.OK, "[]")
            .Route("/repos/owner/repo/actions/runs", HttpStatusCode.OK, """{"workflow_runs":[]}""")
            .Route("/repos/owner/repo/pulls", HttpStatusCode.OK, "[]")
            .Route("/repos/owner/repo/issues", HttpStatusCode.OK, $"[{pullRequestsFromIssuesEndpoint},{actualIssues}]")
            .Route("/repos/owner/repo", HttpStatusCode.OK, """{"default_branch":"main"}""");

        var activity = await Build(handler).GetActivityAsync(RepoRef.Parse("owner/repo").Value, Since);

        activity.Issues.Items.Should().HaveCount(5,
            "forty-five of the fifty raw results were pull requests returned by the issues endpoint, not issues");
        activity.Issues.Truncated.Should().BeTrue(
            "the raw page fetched was a full page of fifty, even though dropping pull requests left only five");
    }

    [Fact]
    public async Task Truncation_of_failed_runs_is_measured_before_filtering_by_start_time()
    {
        var tooOld = string.Join(",", Enumerable.Range(0, 45).Select(i =>
            $"{{\"id\":{i},\"name\":\"ci\",\"conclusion\":\"failure\",\"head_branch\":\"main\",\"run_started_at\":\"2026-09-01T00:00:00Z\"}}"));
        var withinWindow = string.Join(",", Enumerable.Range(45, 5).Select(i =>
            $"{{\"id\":{i},\"name\":\"ci\",\"conclusion\":\"failure\",\"head_branch\":\"main\",\"run_started_at\":\"2026-09-19T06:00:00Z\"}}"));
        var runsJson = "{\"workflow_runs\":[" + tooOld + "," + withinWindow + "]}";

        var handler = new StubHandler()
            .Route("/repos/owner/repo/commits", HttpStatusCode.OK, "[]")
            .Route("/repos/owner/repo/actions/runs", HttpStatusCode.OK, runsJson)
            .Route("/repos/owner/repo/pulls", HttpStatusCode.OK, "[]")
            .Route("/repos/owner/repo/issues", HttpStatusCode.OK, "[]")
            .Route("/repos/owner/repo", HttpStatusCode.OK, """{"default_branch":"main"}""");

        var activity = await Build(handler).GetActivityAsync(RepoRef.Parse("owner/repo").Value, Since);

        activity.FailedRuns.Items.Should().HaveCount(5,
            "only five of the fifty raw runs started within the window");
        activity.FailedRuns.Truncated.Should().BeTrue(
            "the raw page fetched was a full page of fifty, even though filtering by start time left only five");
    }
}
