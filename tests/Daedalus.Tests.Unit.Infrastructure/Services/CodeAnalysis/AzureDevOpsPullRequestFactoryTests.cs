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
    ///     Red: raise <c>MaxErrorBodyBytes</c> on <c>IAzureDevOpsRestApi</c> to 2048 and append
    ///     <c>Encoding.UTF8.GetString(error.Body.Span)</c> to the Status arm of <c>Describe</c>; the text assertion fails.
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
    ///     The second row is a valid URL without <c>_git</c>, with a secret in its user info.
    ///     Red: append <c>repositoryUrl</c> to the failure text; the no-secret assertion fails on the second row.
    ///     Red: drop the word "format" from the failure text; the text assertion fails on both rows.
    ///     Red: on a failed shape check, send the create with placeholder segments before returning the failure;
    ///     the count assertion fails.
    /// </summary>
    [Theory]
    [InlineData("not a url")]
    [InlineData("https://u:SECRET@dev.azure.com/org/project/repo")]
    public async Task A_url_that_is_not_one_is_a_failure_and_sends_nothing(string remote)
    {
        var handler = RecordingHandler.Answering(_ => Created(1));
        var factory = new AzureDevOpsPullRequestFactory(new HttpClient(handler), Tokens("pat-1"), NullLogger<AzureDevOpsPullRequestFactory>.Instance);

        var result = await factory.CreatePullRequestAsync(remote, "feature", "main", "t", "d");

        result.Error.Should().NotContain("SECRET");
        result.Error.Should().Be("Invalid Azure DevOps URL format");
        handler.Count.Should().Be(0);
    }

    /// <summary>
    ///     Today's factory logged the refusal's raw body. Red: raise <c>MaxErrorBodyBytes</c> to 2048, put
    ///     <c>Encoding.UTF8.GetString(created.Error.Body.Span)</c> in a local variable and pass it to a logger message
    ///     (inline, the argument trips CA1873); the log assertion fails.
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

    /// <summary>
    ///     The Azure DevOps client reads no error body at all.
    ///     Red: raise <c>MaxErrorBodyBytes</c> on <c>IAzureDevOpsRestApi</c> to 2048; the body is no longer empty.
    /// </summary>
    [Fact]
    public async Task The_generated_client_leaves_a_refusals_body_empty()
    {
        var handler = RecordingHandler.Answering(_ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("zqx-body-text") });
        var client = new AzureDevOpsRestApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://dev.azure.com/") },
            new ZeroAlloc.Rest.SystemTextJson.SystemTextJsonSerializer(AzureDevOpsJsonContext.Default));

        var result = await client.CreatePullRequestAsync("org", "project", "repo", "7.0", new AzureDevOpsCreatePullRequestBody("a", "b", "t", "d", []), "Basic x", CancellationToken.None);

        result.Error.Kind.Should().Be(ZeroAlloc.Rest.HttpErrorKind.Status);
        result.Error.Body.IsEmpty.Should().BeTrue();
    }

    /// <summary>
    ///     Red: give the <c>TimeoutRejectedException</c> catch a filter that never holds; it falls to the rejection catch and says "was not sent".
    ///     Red: return <c>ex.Message</c> as the timeout text; the text assertion fails on the message.
    /// </summary>
    [Fact]
    public async Task A_pipeline_timeout_is_a_failure_without_the_exceptions_words()
    {
        var factory = FactoryThrowing(new Polly.Timeout.TimeoutRejectedException("zqx-exception-text"));

        var result = await factory.CreatePullRequestAsync(Remote, "feature", "main", "t", "d");

        result.Error.Should().Be("Azure DevOps API error: the request timed out");
    }

    /// <summary>
    ///     Red: give the <c>ExecutionRejectedException</c> catch a filter that never holds; the exception escapes and the
    ///     call throws.
    ///     Red: return <c>ex.Message</c> as the text; the text assertion fails.
    ///     Red: log the type name followed by <c>ex.Message</c>; the no-message assertion fails.
    ///     Red: log <c>ex.Message</c> instead of the type name; the type-name assertion fails.
    /// </summary>
    [Fact]
    public async Task A_rejection_by_the_pipeline_logs_the_exception_type_and_never_its_message()
    {
        var logger = new CapturingLogger();
        var factory = FactoryThrowing(new Polly.CircuitBreaker.BrokenCircuitException("zqx-exception-text"), logger);

        var result = await factory.CreatePullRequestAsync(Remote, "feature", "main", "t", "d");

        result.Error.Should().Be("Azure DevOps API error: the request was not sent");
        logger.Lines.Should().Contain(line => line.Contains(nameof(Polly.CircuitBreaker.BrokenCircuitException), StringComparison.Ordinal));
        logger.Lines.Should().NotContain(line => line.Contains("zqx-exception-text", StringComparison.Ordinal));
    }

    /// <summary>
    ///     A created answer that lacks a field used to throw; now it is a failure.
    ///     Red: give <c>PullRequestId</c> a default of 0 in <c>AzureDevOpsPullRequestDto</c>; row one succeeds with id 0.
    ///     Red: give <c>Url</c> a default of an empty string in <c>AzureDevOpsPullRequestDto</c>; row two succeeds.
    /// </summary>
    [Theory]
    [InlineData("""{"url":"https://dev.azure.com/org/_apis/git/pullrequests/1"}""")]
    [InlineData("""{"pullRequestId":1}""")]
    public async Task An_answer_missing_a_field_is_a_failure(string answer)
    {
        var handler = RecordingHandler.Answering(_ => new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(answer, Encoding.UTF8, "application/json") });
        var factory = new AzureDevOpsPullRequestFactory(new HttpClient(handler), Tokens("pat-1"), NullLogger<AzureDevOpsPullRequestFactory>.Instance);

        var result = await factory.CreatePullRequestAsync(Remote, "feature", "main", "t", "d");

        result.Error.Should().Be("Azure DevOps API error: the answer was not in the expected shape");
    }

    /// <summary>
    ///     Red: remove the Timeout arm of <c>Describe</c>; the text is "could not be read".
    /// </summary>
    [Fact]
    public async Task A_request_cancelled_by_something_else_is_a_timeout_failure()
    {
        var factory = FactoryThrowing(new TaskCanceledException("zqx-exception-text"));

        var result = await factory.CreatePullRequestAsync(Remote, "feature", "main", "t", "d");

        result.Error.Should().Be("Azure DevOps API error: the request timed out");
    }

    /// <summary>
    ///     Red: return <c>error.Message</c> in the Transport arm of <c>Describe</c>; the text carries the exception's words.
    /// </summary>
    [Fact]
    public async Task A_transport_failure_says_so_without_the_exceptions_words()
    {
        var factory = FactoryThrowing(new HttpRequestException("zqx-exception-text"));

        var result = await factory.CreatePullRequestAsync(Remote, "feature", "main", "t", "d");

        result.Error.Should().Be("Azure DevOps API error: Azure DevOps could not be reached");
    }

    /// <summary>
    ///     Red: change the fallback arm of <c>Describe</c> to <c>error.Message</c>; the text carries the parser's words.
    /// </summary>
    [Fact]
    public async Task An_answer_that_is_not_json_is_a_failure_without_the_parsers_words()
    {
        var handler = RecordingHandler.Answering(_ => new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("zqx-not-json", Encoding.UTF8, "application/json") });
        var factory = new AzureDevOpsPullRequestFactory(new HttpClient(handler), Tokens("pat-1"), NullLogger<AzureDevOpsPullRequestFactory>.Instance);

        var result = await factory.CreatePullRequestAsync(Remote, "feature", "main", "t", "d");

        result.Error.Should().Be("Azure DevOps API error: the answer could not be read");
    }

    private static AzureDevOpsPullRequestFactory FactoryThrowing(Exception thrown, Microsoft.Extensions.Logging.ILogger<AzureDevOpsPullRequestFactory>? logger = null) =>
        new(
            new HttpClient(new RecordingHandler((_, _) => throw thrown)),
            Tokens("pat-1"),
            logger ?? NullLogger<AzureDevOpsPullRequestFactory>.Instance);

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
