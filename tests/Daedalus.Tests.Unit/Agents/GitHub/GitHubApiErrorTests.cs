using System.Net;
using System.Text;
using Daedalus.Infrastructure.Services.GitHub;
using static Daedalus.Tests.Unit.Agents.GitHub.GitHubApiTestSupport;

namespace Daedalus.Tests.Unit.Agents.GitHub;

/// <summary>
///     Phase 2.11: what a refusal reads as through the facade, with the mapper attached. A 429 and a 403 with Retry-After
///     are rate limits now, which fixes the spec's defect 5, and no error copies GitHub's message.
/// </summary>
public sealed class GitHubApiErrorTests
{
    private static readonly RepoRef Repo = RepoRef.Parse("owner/repo").Value;

    /// <summary>
    ///     Red: drop the <c>429</c> arm from <c>GitHubErrorMapper</c>; the 429 row is Validation.
    ///     Red: drop <c>retryAfter is not null</c> from the mapper's 403 guard; the secondary row is Forbidden.
    ///     Red: map 404 to a null success for the repository read as for an issue; the 404 row succeeds and
    ///     <c>Error</c> throws.
    ///     Red: drop the status from <c>FromStatus</c>; the status assertion fails.
    /// </summary>
    [Theory]
    [InlineData(404, "", GitHubErrorKind.NotFound)]
    [InlineData(401, "", GitHubErrorKind.Unauthorized)]
    [InlineData(403, "X-RateLimit-Remaining=4999", GitHubErrorKind.Forbidden)]
    [InlineData(403, "X-RateLimit-Remaining=0;X-RateLimit-Reset=1789000000", GitHubErrorKind.RateLimited)]
    [InlineData(403, "Retry-After=60", GitHubErrorKind.RateLimited)]
    [InlineData(429, "Retry-After=60", GitHubErrorKind.RateLimited)]
    [InlineData(500, "", GitHubErrorKind.Server)]
    public async Task A_refusal_has_its_kind_and_status(int status, string headers, GitHubErrorKind kind)
    {
        var handler = new StubHandler().Route("/repos/owner/repo", () => Refusal(status, headers));

        var result = await Build(handler).GetDefaultBranchAsync(Repo);

        result.Error.Kind.Should().Be(kind);
        result.Error.StatusCode.Should().Be(status);
    }

    /// <summary>
    ///     The reset, 07:46:40 UTC, lies after the test clock's 07:00; the mapper clamps a reset already past to now.
    ///     Red: read <c>X-RateLimit-Reset</c> as milliseconds in the mapper; the RetryAt assertion fails.
    ///     Red: format the time with <c>"u"</c>; the message assertion fails.
    /// </summary>
    [Fact]
    public async Task An_exhausted_403_says_when_the_limit_resets()
    {
        var handler = new StubHandler().Route("/repos/owner/repo", () => Refusal(403, "X-RateLimit-Remaining=0;X-RateLimit-Reset=1789804000"));

        var result = await Build(handler).GetDefaultBranchAsync(Repo);

        result.Error.RetryAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1789804000));
        result.Error.Message.Should().Be("GitHub answered 403: rate-limited until 07:46 UTC.");
    }

    /// <summary>
    ///     The body still reaches <c>GitHubSaid</c>, which proves it was read, and never the message.
    ///     Red: build the message from <c>ForAgent()</c> in the mapper; the message assertion fails.
    ///     Red: set <c>MaxErrorBodyBytes = 0</c> on <c>IGitHubRestApi</c>; GitHubSaid is null and its assertion fails.
    /// </summary>
    [Theory]
    [InlineData(404)]
    [InlineData(403)]
    [InlineData(422)]
    [InlineData(500)]
    public async Task An_error_never_copies_GitHubs_message(int status)
    {
        var handler = new StubHandler().Route("/repos/owner/repo", () => Refusal(status, ""));

        var result = await Build(handler).GetDefaultBranchAsync(Repo);

        result.Error.Message.Should().NotContain("zqx-body-text");
        result.Error.GitHubSaid.Should().Be("zqx-body-text");
    }

    private static HttpResponseMessage Refusal(int status, string headers)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent("""{"message":"zqx-body-text"}""", Encoding.UTF8, "application/json"),
        };
        foreach (var header in headers.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = header.Split('=', 2);
            response.Headers.TryAddWithoutValidation(pair[0], pair[1]);
        }

        return response;
    }
}
