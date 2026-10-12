using System.Net;
using System.Text;
using Daedalus.Infrastructure.Services.CodeAnalysis;
using Daedalus.Infrastructure.Services.GitHub;
using Daedalus.Tests.Unit.Infrastructure.Services.GitHub;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Daedalus.Tests.Unit.Infrastructure.Services.CodeAnalysis;

/// <summary>Phase 2.11, ruling P16: the GitHub factory's failure text is GitHubError's message and nothing else.</summary>
public sealed class GitHubPullRequestFactoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 14, 0, 0, TimeSpan.Zero);

    /// <summary>Red: pass <c>result.Error.Kind.ToString()</c> on instead of the message; the text is "RateLimited".</summary>
    [Fact]
    public async Task A_rate_limited_create_fails_with_the_retry_time()
    {
        var handler = RecordingHandler.Answering(_ =>
        {
            var refused = new HttpResponseMessage((HttpStatusCode)429);
            refused.Headers.TryAddWithoutValidation("Retry-After", "300");
            return refused;
        });

        var result = await Factory(handler).CreatePullRequestAsync("https://github.com/owner/repo", "feature", "main", "t", "d");

        result.Error.Should().Be("GitHub answered 429: rate-limited until 14:05 UTC.");
    }

    /// <summary>
    ///     An unexpected exception is a bug, not a failure text a run would show.
    ///     Red: put back the catch-all that returned "Error creating PR: " and the exception's message; the call then
    ///     returns a failure instead of throwing.
    /// </summary>
    [Fact]
    public async Task An_unexpected_exception_is_not_turned_into_a_failure_text()
    {
        var handler = RecordingHandler.Answering(_ => throw new InvalidOperationException("zqx-exception-text"));

        var act = () => Factory(handler).CreatePullRequestAsync("https://github.com/owner/repo", "feature", "main", "t", "d");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>
    ///     The caller's own cancellation is not a failure text either.
    ///     Red: put back a catch-all around the call that returns a failure for any exception; the call returns a failure
    ///     instead of throwing.
    /// </summary>
    [Fact]
    public async Task A_cancelled_caller_gets_the_cancellation_and_not_a_failure_text()
    {
        using var cts = new CancellationTokenSource();
        var handler = new RecordingHandler(async (_, token) =>
        {
            await cts.CancelAsync();
            token.ThrowIfCancellationRequested();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var act = () => Factory(handler).CreatePullRequestAsync("https://github.com/owner/repo", "feature", "main", "t", "d", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    ///     The run shows this text, and phase 2.9 treats a repository slug as content (P30).
    ///     Red: put <c>{repo}</c> back into <c>CreatePullRequestAsync</c>'s <c>Unexpected</c> text; the text names owner/repo.
    /// </summary>
    [Fact]
    public async Task An_unreadable_create_answer_names_no_repository()
    {
        var handler = RecordingHandler.Answering(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("""{"number":1}""", Encoding.UTF8, "application/json"),
        });

        var result = await Factory(handler).CreatePullRequestAsync("https://github.com/owner/repo", "feature", "main", "t", "d");

        result.Error.Should().Be($"GitHub's answer to opening a pull request was not in the expected shape. {GitHubError.MayHaveLanded}");
    }

    /// <summary>
    ///     A configured remote can carry credentials in its user info. The second row is one the old code echoed whole.
    ///     Red: return <c>RepoRef.Parse</c>'s own failure from <c>ParseRepository</c>, as before; every row fails the
    ///     fixed-text assertion.
    ///     Red: the same change; the second row's text contains "SECRET" and the host path.
    ///     Red: fall back to a fixed owner and repository when parsing fails; a request is sent and the count is 1.
    /// </summary>
    [Theory]
    [InlineData("https://u:SECRET@github.com/owner/repo.git")]
    [InlineData("u:SECRET@github.com/team repo")]
    public async Task A_remote_url_never_reaches_the_failure_text(string remote)
    {
        var handler = RecordingHandler.Answering(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var result = await Factory(handler).CreatePullRequestAsync(remote, "feature", "main", "t", "d");

        result.Error.Should().Be(GitHubPullRequestFactory.NotAGitHubUrl);
        result.Error.Should().NotContain("SECRET").And.NotContain("github.com/");
        handler.Count.Should().Be(0);
    }

    /// <summary>Red: put <c>{repo}</c> back into <c>FindOpenPullRequestAsync</c>'s <c>Unexpected</c> text; the text names owner/repo.</summary>
    [Fact]
    public async Task An_unreadable_lookup_answer_names_no_repository()
    {
        var handler = RecordingHandler.Answering(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""[{"number":7}]""", Encoding.UTF8, "application/json"),
        });

        var result = await Factory(handler).FindOpenPullRequestAsync("https://github.com/owner/repo", "manufacture/abc");

        result.Error.Should().Be("GitHub's answer to looking up an open pull request was not in the expected shape.");
    }

    /// <summary>
    ///     Logs are exported over OTLP and phase 2.9 treats a repository slug as content, so neither outcome logs the pull
    ///     request's URL, which names it.
    ///     Red: log <c>result.Value.WebUrl</c> in <c>LogCreated</c> again, as before; a line names owner/repo.
    ///     Red: log <c>result.Error.ForAgent()</c> in <c>LogCreateRefused</c>; a line contains the refusal's words.
    ///     Red: drop the <c>LogCreated</c> call; the created line assertion fails.
    /// </summary>
    [Fact]
    public async Task Neither_outcome_logs_the_pull_request_url_or_what_GitHub_said()
    {
        var created = RecordingHandler.Answering(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent(
                """{"number":5,"url":"https://api.github.com/repos/owner/repo/pulls/5","html_url":"https://github.com/owner/repo/pull/5"}""",
                Encoding.UTF8, "application/json"),
        });
        var refused = RecordingHandler.Answering(_ => new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
        {
            Content = new StringContent("""{"message":"zqx-said"}""", Encoding.UTF8, "application/json"),
        });
        var logger = new CapturingLogger<GitHubPullRequestFactory>();

        var made = await Factory(created, logger).CreatePullRequestAsync("https://github.com/owner/repo", "feature", "main", "t", "d");
        await Factory(refused, logger).CreatePullRequestAsync("https://github.com/owner/repo", "feature", "main", "t", "d");

        made.IsSuccess.Should().BeTrue();
        logger.Lines.Should().Contain(line => line.Contains("Created GitHub PR 5", StringComparison.Ordinal));
        logger.Lines.Should().NotContain(line => line.Contains("owner/repo", StringComparison.Ordinal));
        logger.Lines.Should().NotContain(line => line.Contains("zqx-said", StringComparison.Ordinal));
    }

    private static GitHubPullRequestFactory Factory(HttpMessageHandler handler, ILogger<GitHubPullRequestFactory>? logger = null)
    {
        var clients = Substitute.For<IHttpClientFactory>();
        clients.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));
        var api = new GitHubApi(
            clients, Options.Create(new GitHubOptions()), new GitHubTokenSource(_ => "ghp_example"),
            new FakeTimeProvider(Now), NullLogger<GitHubApi>.Instance);
        return new GitHubPullRequestFactory(api, logger ?? NullLogger<GitHubPullRequestFactory>.Instance);
    }

    /// <summary>Every formatted message and every structured value, so a value logged as a parameter is caught too.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();

        public IReadOnlyList<string> Lines => [.. _lines];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _lines.Enqueue(formatter(state, exception));
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                foreach (var value in values)
                    _lines.Enqueue(value.Value?.ToString() ?? "");
            }
        }
    }
}
