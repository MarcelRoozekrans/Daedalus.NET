using System.Net;
using Daedalus.Agents.GitHub;
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
}
