using System.Net;
using System.Text;
using Daedalus.Infrastructure.Services.GitHub;
using Microsoft.Extensions.Time.Testing;
using ZeroAlloc.Rest;

namespace Daedalus.Tests.Unit.Infrastructure.Services.GitHub;

/// <summary>Phase 2.11, rulings P7 and P8: which <see cref="GitHubErrorKind"/> each failure is, and when to retry.</summary>
public sealed class GitHubErrorMapperTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 13, 0, 0, TimeSpan.Zero);

    /// <summary>
    ///     Red: drop the <c>429</c> arm; the 429 row is Validation.
    ///     Red: drop <c>exhausted</c> from the 403 guard; the exhausted row is Forbidden.
    ///     Red: drop <c>retryAfter is not null</c> from the 403 guard; the secondary-limit row is Forbidden.
    ///     Red: map 410 to Validation; the 410 row fails. Red: map 422 to Conflict; the 422 row fails.
    ///     Red: map <c>&gt;= 500 and &lt;= 599</c> to <c>Transport</c>; the 502 row fails.
    ///     Red: map the status of every row to <c>null</c>; the status assertion fails.
    /// </summary>
    [Theory]
    [InlineData(429, "", GitHubErrorKind.RateLimited)]
    [InlineData(403, "X-RateLimit-Remaining=0", GitHubErrorKind.RateLimited)]
    [InlineData(403, "Retry-After=60", GitHubErrorKind.RateLimited)]
    [InlineData(403, "X-RateLimit-Remaining=12", GitHubErrorKind.Forbidden)]
    [InlineData(401, "", GitHubErrorKind.Unauthorized)]
    [InlineData(404, "", GitHubErrorKind.NotFound)]
    [InlineData(410, "", GitHubErrorKind.NotFound)]
    [InlineData(408, "", GitHubErrorKind.Timeout)]
    [InlineData(409, "", GitHubErrorKind.Conflict)]
    [InlineData(400, "", GitHubErrorKind.Validation)]
    [InlineData(422, "", GitHubErrorKind.Validation)]
    [InlineData(502, "", GitHubErrorKind.Server)]
    public void A_status_maps_to_its_kind(int status, string headers, GitHubErrorKind kind)
    {
        var error = Mapper().Map(Status(status, headers));

        error.Kind.Should().Be(kind);
        error.StatusCode.Should().Be(status);
    }

    /// <summary>
    ///     Red: read the reset as milliseconds; the time fails.
    ///     Red: ignore <c>X-RateLimit-Reset</c>; RetryAt is null.
    /// </summary>
    [Fact]
    public void An_exhausted_403_retries_at_the_reset()
    {
        var error = Mapper().Map(Status(403, "X-RateLimit-Remaining=0;X-RateLimit-Reset=1791727500"));

        error.RetryAt.Should().Be(new DateTimeOffset(2026, 10, 11, 14, 5, 0, TimeSpan.Zero));
        error.Message.Should().Contain("rate-limited until 14:05 UTC");
    }

    /// <summary>Red: prefer <c>X-RateLimit-Reset</c> over <c>Retry-After</c>; RetryAt is the reset instead.</summary>
    [Fact]
    public void Retry_After_wins_over_the_reset()
    {
        var error = Mapper().Map(Status(429, "Retry-After=120;X-RateLimit-Reset=1791727500"));

        error.RetryAt.Should().Be(Now.AddMinutes(2));
    }

    /// <summary>Red: measure <c>Retry-After</c> from <c>TimeProvider.System</c>; RetryAt is the real time, not the fake one.</summary>
    [Fact]
    public void A_secondary_limit_retries_after_Retry_After()
    {
        var error = Mapper().Map(Status(403, "Retry-After=60"));

        error.RetryAt.Should().Be(Now.AddSeconds(60));
    }

    /// <summary>
    ///     Red: map <c>Timeout</c> to Transport; the timeout row fails.
    ///     Red: map <c>Transport</c> to Server; the transport row fails.
    ///     Red: map <c>Deserialization</c> to Server; the deserialization row fails.
    /// </summary>
    [Theory]
    [InlineData(HttpErrorKind.Timeout, 0, GitHubErrorKind.Timeout, null)]
    [InlineData(HttpErrorKind.Transport, 0, GitHubErrorKind.Transport, null)]
    [InlineData(HttpErrorKind.Deserialization, 200, GitHubErrorKind.Unreadable, 200)]
    [InlineData(HttpErrorKind.Deserialization, 0, GitHubErrorKind.Unreadable, null)]
    public void A_failure_without_an_answer_maps_to_its_kind(HttpErrorKind failure, int status, GitHubErrorKind kind, int? expectedStatus)
    {
        var error = Mapper().Map(new HttpError((HttpStatusCode)status, Headers("")) { Kind = failure });

        error.Kind.Should().Be(kind);
        error.StatusCode.Should().Be(expectedStatus);
    }

    /// <summary>
    ///     ZeroAlloc.Rest reads no body for these clients, but a mapper handed one must still not copy it.
    ///     Red: append <c>Encoding.UTF8.GetString(error.Body.Span)</c> to the message; it then contains the text.
    /// </summary>
    [Theory]
    [InlineData(403)]
    [InlineData(422)]
    [InlineData(500)]
    public void The_message_never_copies_a_body(int status)
    {
        var error = Mapper().Map(new HttpError((HttpStatusCode)status, Headers(""))
        {
            Body = Encoding.UTF8.GetBytes("""{"message":"zqx-body-text"}"""),
        });

        error.Message.Should().NotContain("zqx-body-text");
    }

    /// <summary>
    ///     GitHub's own words, for the agent: the top-level message, then each <c>errors[]</c> entry, on one line.
    ///     Red: read only the top-level <c>message</c>, as today's <c>ExtractMessage</c> did; the summary lacks the entry.
    ///     Red: join with ", " instead of "; "; the summary fails.
    /// </summary>
    [Fact]
    public void What_GitHub_said_is_summarised_from_its_message_and_errors()
    {
        var error = Mapper().Map(new HttpError(HttpStatusCode.UnprocessableEntity, Headers(""))
        {
            Body = Encoding.UTF8.GetBytes("""
                {"message":"Validation Failed","errors":[{"resource":"PullRequest","code":"custom","message":"A pull request already exists for owner:feature."},"plain text error"],"documentation_url":"https://docs.github.com"}
                """),
        });

        error.GitHubSaid.Should().Be("Validation Failed; custom: A pull request already exists for owner:feature.; plain text error");
    }

    /// <summary>
    ///     Red: drop the length cap; the summary is 1000 characters.
    ///     Red: cut without the trailing ellipsis; the end assertion fails.
    ///     Red: keep control characters; the newline survives and the last assertion fails.
    /// </summary>
    [Fact]
    public void What_GitHub_said_is_one_line_of_at_most_300_characters()
    {
        var error = Mapper().Map(new HttpError(HttpStatusCode.Forbidden, Headers(""))
        {
            Body = Encoding.UTF8.GetBytes($$"""{"message":"line one\nline two {{new string('x', 1000)}}"}"""),
        });

        error.GitHubSaid!.Length.Should().Be(GitHubErrorMapper.MaxSaidLength);
        error.GitHubSaid.Should().EndWith("…");
        error.GitHubSaid.Should().StartWith("line one line two x");
    }

    /// <summary>
    ///     Red: parse a truncated body anyway; the summary is then partial text instead of null.
    ///     Red: return the raw text when the body is not JSON; the HTML row fails.
    /// </summary>
    [Fact]
    public void A_body_that_is_truncated_or_not_json_gives_no_summary()
    {
        var truncated = Mapper().Map(new HttpError(HttpStatusCode.Forbidden, Headers(""))
        {
            Body = Encoding.UTF8.GetBytes("""{"message":"cut"}"""),
            BodyTruncated = true,
        });
        var html = Mapper().Map(new HttpError(HttpStatusCode.BadGateway, Headers(""))
        {
            Body = Encoding.UTF8.GetBytes("<html>proxy</html>"),
        });

        truncated.GitHubSaid.Should().BeNull();
        html.GitHubSaid.Should().BeNull();
    }

    /// <summary>
    ///     GitHub documents secondary limits that carry neither Retry-After nor an exhausted remaining count.
    ///     Red: drop the body check from the 403 guard; the kind is Forbidden.
    ///     Red: drop the 60-second fallback; RetryAt is null.
    /// </summary>
    [Fact]
    public void A_403_whose_body_names_a_secondary_limit_is_rate_limited_for_a_minute()
    {
        var error = Mapper().Map(new HttpError(HttpStatusCode.Forbidden, Headers(""))
        {
            Body = Encoding.UTF8.GetBytes("""{"message":"You have exceeded a secondary rate limit. Please wait a few minutes before you try again."}"""),
        });

        error.Kind.Should().Be(GitHubErrorKind.RateLimited);
        error.RetryAt.Should().Be(Now.AddSeconds(60));
    }

    /// <summary>Red: fall back to 60 seconds even when Retry-After is present; RetryAt is a minute instead of two.</summary>
    [Fact]
    public void A_secondary_limit_with_Retry_After_keeps_its_header_time()
    {
        var error = Mapper().Map(new HttpError(HttpStatusCode.TooManyRequests, Headers("Retry-After=120"))
        {
            Body = Encoding.UTF8.GetBytes("""{"message":"You have exceeded a secondary rate limit."}"""),
        });

        error.RetryAt.Should().Be(Now.AddMinutes(2));
    }

    private static GitHubErrorMapper Mapper() => new(new FakeTimeProvider(Now));

    private static HttpError Status(int status, string headers) => new((HttpStatusCode)status, Headers(headers));

    private static Dictionary<string, IReadOnlyList<string>> Headers(string spec) =>
        spec.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(header => header.Split('=', 2))
            .ToDictionary(pair => pair[0], pair => (IReadOnlyList<string>)[pair[1]], StringComparer.OrdinalIgnoreCase);
}
