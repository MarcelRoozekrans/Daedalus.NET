using System.Net;
using System.Text;
using Daedalus.Infrastructure.Services.GitHub;
using static Daedalus.Tests.Unit.Agents.GitHub.GitHubApiTestSupport;

namespace Daedalus.Tests.Unit.Agents.GitHub;

/// <summary>
///     Phase 2.11 characterisation of today's error text, so Task 5's move to kinds is a visible flip. The 429 row and the
///     403-with-Retry-After row are the spec's defect 5: neither reads as a rate limit today. The last test pins the privacy
///     defect Task 5 removes: today's text copies GitHub's own message.
/// </summary>
public sealed class GitHubApiErrorTests
{
    private static readonly RepoRef Repo = RepoRef.Parse("owner/repo").Value;

    /// <summary>
    ///     Red: drop the 404 branch from <c>MapError</c>; the 404 row reads "failed with status 404".
    ///     Red: drop the 403 branch; the forbidden rows read "failed with status 403".
    ///     Red: read <c>X-RateLimit-Remaining</c> as exhausted at <c>1</c>; the exhausted row reads "forbidden".
    ///     Red: format the reset with <c>"o"</c>; the exhausted row's time fails.
    ///     Red: treat a 429 as rate-limited; the 429 row fails.
    ///     Red: drop the status number from <c>MapError</c>'s fallback text; the 401, 429 and 500 rows fail.
    /// </summary>
    [Theory]
    [InlineData(404, "", "does not exist")]
    [InlineData(401, "", "failed with status 401")]
    [InlineData(403, "X-RateLimit-Remaining=4999", "GitHub request forbidden")]
    [InlineData(403, "X-RateLimit-Remaining=0;X-RateLimit-Reset=1789000000", "rate limit exceeded; it resets at 2026-09-10 00:26:40Z")]
    [InlineData(403, "Retry-After=60", "GitHub request forbidden")]
    [InlineData(429, "Retry-After=60", "failed with status 429")]
    [InlineData(500, "", "failed with status 500")]
    public async Task A_refusal_reads_as_today(int status, string headers, string expected)
    {
        var handler = new StubHandler().Route("/repos/owner/repo", () => Refusal(status, headers));

        var result = await Build(handler).GetDefaultBranchAsync(Repo);

        result.Error.Should().Contain(expected);
    }

    /// <summary>Red: build the text without <c>ExtractMessage</c>; the body text is then missing.</summary>
    [Theory]
    [InlineData(404)]
    [InlineData(403)]
    [InlineData(422)]
    [InlineData(500)]
    public async Task Todays_error_copies_GitHubs_message(int status)
    {
        var handler = new StubHandler().Route("/repos/owner/repo", () => Refusal(status, ""));

        var result = await Build(handler).GetDefaultBranchAsync(Repo);

        result.Error.Should().Contain("zqx-body-text");
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
