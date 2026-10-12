using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Daedalus.Infrastructure.Services.CodeAnalysis;
using Daedalus.Infrastructure.Services.GitHub;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Daedalus.Tests.Unit.Infrastructure.Services.GitHub;

/// <summary>
///     Phase 2.11, ruling P9: what GitHub said goes to the agent tools only. Logs are exported over OTLP, and the factory's
///     text becomes the publisher's run-visible message, so neither may carry it.
/// </summary>
public sealed class GitHubSaidPrivacyTests
{
    private static readonly RepoRef Repo = RepoRef.Parse("owner/repo").Value;

    /// <summary>
    ///     Red: log <c>error.ForAgent()</c> in <c>GitHubApi.Unavailable</c> instead of the kind; the log assertion fails.
    ///     Red: fail the category with <c>error.ForAgent()</c>; the reason assertion fails.
    /// </summary>
    [Fact]
    public async Task A_digest_category_failure_logs_and_reports_no_GitHub_words()
    {
        var logger = new CapturingLogger<GitHubApi>();
        var api = Api(Refusing(), logger);

        var activity = await api.GetActivityAsync(Repo, DateTime.UtcNow.AddDays(-1));

        logger.Lines.Should().NotContain(line => line.Contains("zqx-said", StringComparison.Ordinal));
        activity.FailedRuns.Unavailable.Should().NotContain("zqx-said");
    }

    /// <summary>
    ///     Red: log <c>result.Error.ForAgent()</c> in <c>GitHubPullRequestFactory</c>; the log assertion fails.
    ///     Red: pass <c>result.Error.ForAgent()</c> on as the failure text; the text assertion fails.
    /// </summary>
    [Fact]
    public async Task The_pull_request_factory_logs_and_passes_on_no_GitHub_words()
    {
        var logger = new CapturingLogger<GitHubPullRequestFactory>();
        var factory = new GitHubPullRequestFactory(Api(Refusing(), new CapturingLogger<GitHubApi>()), logger);

        var result = await factory.CreatePullRequestAsync("https://github.com/owner/repo", "feature", "main", "t", "d");

        logger.Lines.Should().NotContain(line => line.Contains("zqx-said", StringComparison.Ordinal));
        result.Error.Should().NotContain("zqx-said");
    }

    private static RecordingHandler Refusing() => RecordingHandler.Answering(_ => new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
    {
        Content = new StringContent("""{"message":"zqx-said"}""", Encoding.UTF8, "application/json"),
    });

    private static GitHubApi Api(HttpMessageHandler handler, ILogger<GitHubApi> logger)
    {
        var clients = Substitute.For<IHttpClientFactory>();
        clients.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));
        return new GitHubApi(clients, Options.Create(new GitHubOptions()), new GitHubTokenSource(_ => "ghp_example"), TimeProvider.System, logger);
    }

    /// <summary>Every formatted message and every structured value, so a value logged as a parameter is caught too.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<string> _lines = new();

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
