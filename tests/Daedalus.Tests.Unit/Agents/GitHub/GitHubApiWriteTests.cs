using System.Net;
using Daedalus.Domain.CodeAnalysis;
using Daedalus.Infrastructure.Services.GitHub;
using static Daedalus.Tests.Unit.Agents.GitHub.GitHubApiTestSupport;

namespace Daedalus.Tests.Unit.Agents.GitHub;

public class GitHubApiWriteTests
{
    [Fact]
    public async Task Commenting_posts_to_the_issue_comments_endpoint()
    {
        var handler = new StubHandler().Route("/issues/7/comments", HttpStatusCode.Created, """{"id":1}""");

        var result = await Build(handler).CommentAsync(RepoRef.Parse("owner/repo").Value, 7, "a note");

        result.IsSuccess.Should().BeTrue();
        var sent = handler.Requests.Should().ContainSingle().Subject;
        sent.Method.Should().Be(HttpMethod.Post);
        sent.RequestUri!.AbsolutePath.Should().Be("/repos/owner/repo/issues/7/comments");
    }

    [Fact]
    public async Task A_failed_write_surfaces_and_is_not_retried()
    {
        var handler = new StubHandler().Route("/issues/7/comments", HttpStatusCode.UnprocessableEntity,
            """{"message":"Validation Failed"}""");

        var result = await Build(handler).CommentAsync(RepoRef.Parse("owner/repo").Value, 7, "a note");

        result.IsFailure.Should().BeTrue();
        handler.Requests.Should().ContainSingle("a retried comment is a double comment");
    }

    [Fact]
    public async Task Closing_an_issue_patches_state_to_closed()
    {
        var handler = new StubHandler().Route("/issues/7", HttpStatusCode.OK, """{"number":7,"state":"closed"}""");

        await Build(handler).CloseIssueAsync(RepoRef.Parse("owner/repo").Value, 7);

        var sent = handler.Requests.Should().ContainSingle().Subject;
        sent.Method.Should().Be(HttpMethod.Patch);
        (await sent.Content!.ReadAsStringAsync()).Should().Contain("closed");
    }

    [Fact]
    public async Task Adding_a_label_posts_to_the_labels_endpoint()
    {
        var handler = new StubHandler().Route("/issues/7/labels", HttpStatusCode.OK, """[{"name":"stale"}]""");

        var result = await Build(handler).AddLabelAsync(RepoRef.Parse("owner/repo").Value, 7, "stale");

        result.IsSuccess.Should().BeTrue();
        handler.Requests.Should().ContainSingle().Which.RequestUri!.AbsolutePath
            .Should().Be("/repos/owner/repo/issues/7/labels");
    }

    [Fact]
    public async Task A_write_with_no_token_sends_nothing()
    {
        var handler = new StubHandler();

        var result = await Build(handler, token: null).CommentAsync(RepoRef.Parse("owner/repo").Value, 7, "a note");

        result.IsFailure.Should().BeTrue();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Creating_a_pull_request_posts_to_the_pulls_endpoint_and_parses_the_response()
    {
        var handler = new StubHandler().Route("/pulls", HttpStatusCode.Created,
            """{"number":42,"url":"https://api.github.com/repos/owner/repo/pulls/42","html_url":"https://github.com/owner/repo/pull/42"}""");

        var result = await Build(handler).CreatePullRequestAsync(
            RepoRef.Parse("owner/repo").Value, "feature", "main", "a title", "a description");

        result.IsSuccess.Should().BeTrue();
        result.Value.PullRequestId.Should().Be("42");
        result.Value.PullRequestUrl.Should().Be("https://api.github.com/repos/owner/repo/pulls/42");
        result.Value.WebUrl.Should().Be("https://github.com/owner/repo/pull/42");
        result.Value.Status.Should().Be(PullRequestStatus.Open);

        var sent = handler.Requests.Should().ContainSingle().Subject;
        sent.Method.Should().Be(HttpMethod.Post);
        sent.RequestUri!.AbsolutePath.Should().Be("/repos/owner/repo/pulls");
        var body = await sent.Content!.ReadAsStringAsync();
        body.Should().Contain("\"head\":\"feature\"").And.Contain("\"base\":\"main\"").And.Contain("\"title\":\"a title\"");
    }

    [Fact]
    public async Task A_failed_pull_request_creation_surfaces_and_is_not_retried()
    {
        var handler = new StubHandler().Route("/pulls", HttpStatusCode.UnprocessableEntity,
            """{"message":"Validation Failed"}""");

        var result = await Build(handler).CreatePullRequestAsync(
            RepoRef.Parse("owner/repo").Value, "feature", "main", "a title", "a description");

        result.IsFailure.Should().BeTrue();
        handler.Requests.Should().ContainSingle("a retried pull request create would open a duplicate");
    }

    [Fact]
    public async Task Creating_a_pull_request_with_no_token_sends_nothing()
    {
        var handler = new StubHandler();

        var result = await Build(handler, token: null).CreatePullRequestAsync(
            RepoRef.Parse("owner/repo").Value, "feature", "main", "a title", "a description");

        result.IsFailure.Should().BeTrue();
        handler.Requests.Should().BeEmpty();
    }

    /// <summary>Red: send to <c>/pulls</c>, or drop <c>body</c> from the payload; an assertion fails.</summary>
    [Fact]
    public async Task Creating_an_issue_posts_title_and_body_and_returns_number_and_link()
    {
        var handler = new StubHandler().Route("/repos/owner/repo/issues", HttpStatusCode.Created,
            """{"number":12,"html_url":"https://github.com/owner/repo/issues/12"}""");

        var result = await Build(handler).CreateIssueAsync(RepoRef.Parse("owner/repo").Value, "A title", "A body");

        result.Value.Should().Be(new CreatedIssue(12, new Uri("https://github.com/owner/repo/issues/12")));
        var sent = handler.Requests.Should().ContainSingle().Subject;
        sent.Method.Should().Be(HttpMethod.Post);
        sent.RequestUri!.AbsolutePath.Should().Be("/repos/owner/repo/issues");
        var payload = await sent.Content!.ReadAsStringAsync();
        payload.Should().Contain("\"title\":\"A title\"").And.Contain("\"body\":\"A body\"");
    }

    /// <summary>Red: retry on failure; two requests are then recorded.</summary>
    [Fact]
    public async Task A_failed_issue_create_surfaces_and_is_not_retried()
    {
        var handler = new StubHandler().Route("/repos/owner/repo/issues", HttpStatusCode.Forbidden,
            """{"message":"Resource not accessible by personal access token"}""");

        var result = await Build(handler).CreateIssueAsync(RepoRef.Parse("owner/repo").Value, "A title", "A body");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Resource not accessible");
        handler.Requests.Should().ContainSingle();
    }

    /// <summary>Red: drop the length check from <c>CreateIssueAsync</c>; the 257-character case sends a request.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("TOO_LONG")]
    public async Task An_issue_title_that_is_blank_or_too_long_fails_and_sends_nothing(string title)
    {
        var handler = new StubHandler();
        var effective = string.Equals(title, "TOO_LONG", StringComparison.Ordinal) ? new string('x', GitHubApi.MaxIssueTitleLength + 1) : title;

        var result = await Build(handler).CreateIssueAsync(RepoRef.Parse("owner/repo").Value, effective, "body");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("1 to 256 characters");
        handler.Requests.Should().BeEmpty();
    }

    /// <summary>Red: read <c>html_url</c> with a null-tolerant fallback; the malformed and missing cases then succeed.</summary>
    [Theory]
    [InlineData("""{"number":12,"html_url":"not a url"}""")]
    [InlineData("""{"number":12}""")]
    public async Task A_created_issue_without_a_usable_link_is_a_failure(string json)
    {
        var handler = new StubHandler().Route("/repos/owner/repo/issues", HttpStatusCode.Created, json);

        var result = await Build(handler).CreateIssueAsync(RepoRef.Parse("owner/repo").Value, "A title", "A body");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("not in the expected shape");
    }
}
