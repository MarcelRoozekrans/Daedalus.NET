using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Daedalus.Application.Services.CodeAnalysis;
using Daedalus.Domain.CodeAnalysis;
using Daedalus.Infrastructure.Extensions;
using Daedalus.Infrastructure.Services.CodeAnalysis;
using Daedalus.Tests.Unit.Infrastructure.Services.GitHub;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Daedalus.Tests.Unit.Infrastructure.Services.CodeAnalysis;

/// <summary>Phase 2.11: Azure DevOps's pull request create over a generated client, with the token per call.</summary>
public sealed class AzureDevOpsPullRequestFactoryTests
{
    private const string Remote = "https://dev.azure.com/org/My%20Project/_git/repo";

    /// <summary>
    ///     Red: name the token's header <c>X-Authorization</c> in <c>IAzureDevOpsRestApi</c>; the authorization assertion fails.
    ///     Red: set the token on <c>httpClient.DefaultRequestHeaders</c> as well, as before; the shared-headers assertion fails.
    ///     Red: pass the escaped segments to the route; the path is sent as <c>My%2520Project</c>.
    ///     Red: send api-version 7.1; the query assertion fails.
    ///     Red: name the body's <c>SourceRefName</c> <c>SourceBranch</c>; the body assertion fails.
    ///     Red: build the web URL from the unescaped segments; the web URL is <c>My Project</c> and fails.
    /// </summary>
    [Fact]
    public async Task A_pull_request_is_created_with_a_per_call_token_on_the_project_path()
    {
        var handler = RecordingHandler.Answering(_ => Created(42));
        var http = new HttpClient(handler);
        var factory = new AzureDevOpsPullRequestFactory(http, Tokens("pat-1"), NullLogger<AzureDevOpsPullRequestFactory>.Instance);

        var result = await factory.CreatePullRequestAsync(Remote, "feature", "main", "a title", "a description");

        var sent = handler.Sent[0];
        sent.Headers["Authorization"].Should().Be(Basic("pat-1"));
        http.DefaultRequestHeaders.Authorization.Should().BeNull();
        sent.Uri.AbsolutePath.Should().Be("/org/My%20Project/_apis/git/repositories/repo/pullrequests");
        sent.Uri.Query.Should().Be("?api-version=7.0");
        JsonNode.DeepEquals(
                JsonNode.Parse(sent.Body!),
                JsonNode.Parse("""{"sourceRefName":"refs/heads/feature","targetRefName":"refs/heads/main","title":"a title","description":"a description","reviewers":[]}"""))
            .Should().BeTrue(sent.Body);
        result.Value.WebUrl.Should().Be("https://dev.azure.com/org/My%20Project/_git/repo/pullrequest/42");
    }

    /// <summary>
    ///     Both requests are held until both have arrived, so they are in flight together.
    ///     Red: keep the first call's authorization in a field and send it on every later call; both carry pat-1.
    /// </summary>
    [Fact]
    public async Task Two_calls_at_once_each_send_their_own_token()
    {
        var bothArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = 0;
        var handler = new RecordingHandler(async (_, ct) =>
        {
            if (Interlocked.Increment(ref arrived) == 2)
                bothArrived.SetResult();
            await bothArrived.Task.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            return Created(1);
        });
        var factory = new AzureDevOpsPullRequestFactory(new HttpClient(handler), Tokens("pat-1", "pat-2"), NullLogger<AzureDevOpsPullRequestFactory>.Instance);

        await Task.WhenAll(
            factory.CreatePullRequestAsync(Remote, "a", "main", "t", "d"),
            factory.CreatePullRequestAsync(Remote, "b", "main", "t", "d"));

        handler.Sent.Select(sent => sent.Headers["Authorization"]).Should().BeEquivalentTo([Basic("pat-1"), Basic("pat-2")]);
    }

    /// <summary>
    ///     Red: drop <c>NeverRetry</c> from <c>AddAzureDevOpsPullRequestFactory</c>; the create is sent 4 times, after the
    ///     standard backoff.
    ///     Red: append the response body to the failure text; the text assertion fails.
    /// </summary>
    [Fact]
    public async Task A_create_is_sent_once_when_Azure_DevOps_answers_503_and_its_body_is_not_repeated()
    {
        var handler = RecordingHandler.Answering(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("zqx-body-text") });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Tokens("pat-1"));
        services.AddAzureDevOpsPullRequestFactory();
        services.ConfigureHttpClientDefaults(builder => builder.ConfigurePrimaryHttpMessageHandler(() => handler));
        await using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<AzureDevOpsPullRequestFactory>().CreatePullRequestAsync(Remote, "feature", "main", "t", "d");

        result.Error.Should().Be("Azure DevOps API error: ServiceUnavailable");
        handler.Count.Should().Be(1);
    }

    /// <summary>
    ///     Red: parse with <c>new Uri(repositoryUrl)</c> and no catch, as before the catch-all was removed; the call throws.
    ///     Red: make <c>TryParse</c> return <c>true</c> with empty segments when <c>Uri.TryCreate</c> fails; a request is
    ///     sent and the count is 1.
    /// </summary>
    [Fact]
    public async Task A_url_that_is_not_one_is_a_failure_and_sends_nothing()
    {
        var handler = RecordingHandler.Answering(_ => Created(1));
        var factory = new AzureDevOpsPullRequestFactory(new HttpClient(handler), Tokens("pat-1"), NullLogger<AzureDevOpsPullRequestFactory>.Instance);

        var result = await factory.CreatePullRequestAsync("not a url", "feature", "main", "t", "d");

        result.Error.Should().Be("Invalid Azure DevOps URL format");
        handler.Count.Should().Be(0);
    }

    /// <summary>
    ///     Today's factory logged the refusal's raw body. Red: log <c>await response.Content.ReadAsStringAsync()</c> again,
    ///     after raising <c>MaxErrorBodyBytes</c> and logging <c>Encoding.UTF8.GetString(created.Error.Body.Span)</c>; the
    ///     log assertion fails.
    /// </summary>
    [Fact]
    public async Task A_refusal_logs_its_status_and_never_its_body()
    {
        var handler = RecordingHandler.Answering(_ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("zqx-body-text") });
        var logger = new CapturingLogger();
        var factory = new AzureDevOpsPullRequestFactory(new HttpClient(handler), Tokens("pat-1"), logger);

        await factory.CreatePullRequestAsync(Remote, "feature", "main", "t", "d");

        logger.Lines.Should().NotContain(line => line.Contains("zqx-body-text", StringComparison.Ordinal));
    }

    /// <summary>
    ///     The caller's own cancellation is theirs, not a failure to show on a run.
    ///     Red: add <c>catch (Exception)</c> returning a failure around the call; the call no longer throws.
    /// </summary>
    [Fact]
    public async Task The_callers_cancellation_propagates_instead_of_becoming_a_failure()
    {
        using var cts = new CancellationTokenSource();
        var handler = new RecordingHandler(async (_, ct) =>
        {
            await cts.CancelAsync();
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            return Created(1);
        });
        var factory = new AzureDevOpsPullRequestFactory(new HttpClient(handler), Tokens("pat-1"), NullLogger<AzureDevOpsPullRequestFactory>.Instance);

        var act = () => factory.CreatePullRequestAsync(Remote, "feature", "main", "t", "d", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<AzureDevOpsPullRequestFactory>
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyList<string> Lines => [.. _lines];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _lines.Enqueue(formatter(state, exception));
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                foreach (var value in values)
                    _lines.Enqueue(value.Value?.ToString() ?? "");
            }
        }
    }

    private static IRepositoryAuthenticationProvider Tokens(params string[] tokens)
    {
        var queue = new ConcurrentQueue<string>(tokens);
        var provider = Substitute.For<IRepositoryAuthenticationProvider>();
        provider.GetAuthTokenAsync(RepositoryPlatform.AzureDevOps, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(Result<string>.Success(queue.TryDequeue(out var token) ? token : tokens[^1])));
        return provider;
    }

    private static HttpResponseMessage Created(int id) => new(HttpStatusCode.Created)
    {
        Content = new StringContent(
            $$"""{"pullRequestId":{{id}},"url":"https://dev.azure.com/org/_apis/git/pullrequests/{{id}}"}""", Encoding.UTF8, "application/json"),
    };

    private static string Basic(string pat) => "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + pat));
}
