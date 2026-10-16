using System.Net;
using System.Text;
using System.Text.Json;
using Daedalus.Application.DTOs;
using Daedalus.Web.Services;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Daedalus.Tests.Unit.Web;

/// <summary>
///     The Git Repositories client keeps the page's own failure text: a 403 is a state, any other refusal names the status
///     the server answered, never the server's detail, and a timeout is told apart from the page going away (R2).
/// </summary>
public sealed class RepositoriesApiClientTests
{
    private const string Detail = "Refused by the test.";

    private static readonly CreateRepositoryConfigurationDto Create = new()
    {
        Name = "atlas",
        Url = "https://github.com/acme/atlas",
        Platform = "GitHub",
        DefaultBranch = "main",
    };

    private static readonly UpdateRepositoryConfigurationDto Update = new()
    {
        Name = "atlas",
        Url = "https://github.com/acme/atlas",
        DefaultBranch = "main",
        IsActive = true,
    };

    /// <summary>A server that never answers, until the request is cancelled.</summary>
    private static async Task<HttpResponseMessage> Hang(HttpRequestMessage request, CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return new HttpResponseMessage(HttpStatusCode.OK);
    }

    private static RepositoriesApiClient Client(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond, TimeSpan? timeout = null)
    {
        var http = new HttpClient(new StubHandler(respond)) { BaseAddress = new Uri("http://localhost") };
        if (timeout is { } t)
        {
            http.Timeout = t;
        }

        return new RepositoriesApiClient(http);
    }

    private static RepositoriesApiClient Client(Func<HttpResponseMessage> respond) =>
        Client((_, _) => Task.FromResult(respond()));

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>A refusal with a ProblemDetails body, whose detail the page must not show.</summary>
    private static HttpResponseMessage Refusal(HttpStatusCode code, string reason) =>
        new(code)
        {
            ReasonPhrase = reason,
            Content = new StringContent($$"""{"title":"x","status":{{(int)code}},"detail":"{{Detail}}"}""", Encoding.UTF8, "application/problem+json"),
        };

    /// <summary>
    ///     A 403 is the page's Forbidden state, a success with no list, not a failure.
    ///     Red: drop the 403 branch in GetAllAsync; the read fails with "The server answered 403 Forbidden.".
    ///     Red: build <c>Denied</c> with an empty list; Repositories is empty, not null.
    /// </summary>
    [Fact]
    public async Task A_403_is_the_forbidden_state()
    {
        var result = await Client(() => Refusal(HttpStatusCode.Forbidden, "Forbidden")).GetAllAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Forbidden.Should().BeTrue();
        result.Value.Repositories.Should().BeNull();
    }

    /// <summary>
    ///     Any other refusal reads "The server answered" with the status and its reason, as the page says it today, and not
    ///     the server's detail.
    ///     Red: fail with the ProblemDetails detail, as ApiClient.ReadFailureReasonAsync does; the error is "Refused by the
    ///     test.".
    ///     Red: drop the reason, <c>(int)response.StatusCode</c> in place of HttpStatusText.Of; the error reads "The server
    ///     answered 500.".
    /// </summary>
    [Fact]
    public async Task A_500_reads_the_server_answered_500()
    {
        var result = await Client(() => Refusal(HttpStatusCode.InternalServerError, "Internal Server Error"))
            .GetAllAsync(CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("The server answered 500 Internal Server Error.");
    }

    /// <summary>
    ///     The list binds from the API's camelCase.
    ///     Red: read the body with <c>JsonSerializer.Deserialize</c> and no options, case-sensitive; the name is empty.
    ///     Red: return <c>Denied</c> in place of <c>Loaded</c>; Forbidden is true.
    /// </summary>
    [Fact]
    public async Task A_camel_case_list_binds()
    {
        var result = await Client(() => Json(HttpStatusCode.OK, """[{"id":"6f1c7b0e-0000-0000-0000-000000000001","name":"atlas","url":"https://github.com/acme/atlas","isActive":true}]"""))
            .GetAllAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Forbidden.Should().BeFalse();
        result.Value.Repositories.Should().ContainSingle().Which.Name.Should().Be("atlas");
    }

    /// <summary>
    ///     The client's own timeout is reported as the page reports it; the page going away, its token cancelled, is the
    ///     cancelled failure an effect drops, never the timeout text.
    ///     Red: drop the timeout catch in GetAllAsync; the timed-out read throws.
    ///     Red: drop the catch filtered on the caller's token; the cancelled read reads "The request timed out.".
    /// </summary>
    [Fact]
    public async Task A_timeout_is_reported_and_a_cancel_is_not()
    {
        var timedOut = await Client(Hang, TimeSpan.FromMilliseconds(50)).GetAllAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var cancelled = await Client(Hang).GetAllAsync(cts.Token);

        timedOut.Error.Should().Be(RepositoriesApiClient.TimedOut);
        cancelled.Error.Should().Be(RepositoriesApiClient.Cancelled);
    }

    /// <summary>
    ///     A body that is not the list is a failure with the reader's message, as the page shows it.
    ///     Red: drop JsonException from GetAllAsync's catch; the read throws.
    /// </summary>
    [Fact]
    public async Task A_body_that_is_not_a_list_is_a_failure()
    {
        var result = await Client(() => Json(HttpStatusCode.OK, "{\"oops\":")).GetAllAsync(CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>Each write by name, through <paramref name="client"/>.</summary>
    private static Task<Result> Write(RepositoriesApiClient client, string write, CancellationToken ct = default) => write switch
    {
        "Create" => client.CreateAsync(Create, ct),
        "Update" => client.UpdateAsync(Guid.NewGuid(), Update, ct),
        "Delete" => client.DeleteAsync(Guid.NewGuid(), ct),
        "TestConnection" => client.TestConnectionAsync(Guid.NewGuid(), ct),
        _ => throw new ArgumentOutOfRangeException(nameof(write), write, "Not a write."),
    };

    public static TheoryData<string> Writes() => new() { "Create", "Update", "Delete", "TestConnection" };

    /// <summary>
    ///     A refused write carries the status the server answered, which the page puts after "Failed to … repository.".
    ///     Red: fail a refused write with the ProblemDetails detail; the error is "Refused by the test.".
    ///     Red: <c>(int)response.StatusCode</c> in place of HttpStatusText.Of in the write helper; the reason is missing.
    /// </summary>
    [Theory]
    [MemberData(nameof(Writes))]
    public async Task A_refused_write_carries_its_status(string write)
    {
        var forbidden = await Write(Client(() => Refusal(HttpStatusCode.Forbidden, "Forbidden")), write);
        var failed = await Write(Client(() => Refusal(HttpStatusCode.InternalServerError, "Internal Server Error")), write);

        forbidden.Error.Should().Be("The server answered 403 Forbidden.");
        failed.Error.Should().Be("The server answered 500 Internal Server Error.");
    }

    /// <summary>
    ///     A write that never got an answer is <see cref="RepositoriesApiClient.NoAnswer"/>, so the page keeps its own text
    ///     for it: "Failed to … repository." alone, or "Could not test connection." at Error level.
    ///     Red: fail with the exception's message in the write helper; the error is "connection refused".
    /// </summary>
    [Theory]
    [MemberData(nameof(Writes))]
    public async Task A_write_with_no_answer_is_NoAnswer(string write)
    {
        var client = Client((_, _) => throw new HttpRequestException("connection refused"));

        var result = await Write(client, write);

        result.Error.Should().Be(RepositoriesApiClient.NoAnswer);
    }

    /// <summary>
    ///     A write that runs into the client's own timeout, with no cancel from its caller, is
    ///     <see cref="RepositoriesApiClient.NoAnswer"/>, as the page's catch-all once made it "Failed to … repository.".
    ///     Red: drop <c>or OperationCanceledException</c> from the write helper's filter; the timed-out write throws.
    /// </summary>
    [Theory]
    [MemberData(nameof(Writes))]
    public async Task A_timed_out_write_is_NoAnswer(string write)
    {
        var result = await Write(Client(Hang, TimeSpan.FromMilliseconds(50)), write);

        result.Error.Should().Be(RepositoriesApiClient.NoAnswer);
    }

    /// <summary>
    ///     A write whose body cannot be written is <see cref="RepositoriesApiClient.NoAnswer"/>, not an exception. JSON
    ///     content is serialized as the handler reads it, so the handler throws what the serializer would.
    ///     Red: drop <c>or JsonException</c> from the write helper's filter; the JsonException cases throw.
    ///     Red: drop <c>or NotSupportedException</c> from the write helper's filter; the NotSupportedException cases throw.
    /// </summary>
    [Theory]
    [MemberData(nameof(BadBodies))]
    public async Task A_write_whose_body_cannot_be_written_is_NoAnswer(string write, string exception)
    {
        Exception thrown = string.Equals(exception, nameof(JsonException), StringComparison.Ordinal) ? new JsonException("bad body") : new NotSupportedException("bad body");
        var client = Client((_, _) => throw thrown);

        var result = await Write(client, write);

        result.Error.Should().Be(RepositoriesApiClient.NoAnswer);
    }

    public static TheoryData<string, string> BadBodies()
    {
        var data = new TheoryData<string, string>();
        foreach (var write in new[] { "Create", "Update", "Delete", "TestConnection" })
        {
            data.Add(write, nameof(JsonException));
            data.Add(write, nameof(NotSupportedException));
        }

        return data;
    }

    /// <summary>
    ///     A write whose caller went away is the cancelled failure, not the no-answer one.
    ///     Red: drop the catch filtered on the caller's token in the write helper; the error is NoAnswer.
    /// </summary>
    [Theory]
    [MemberData(nameof(Writes))]
    public async Task A_cancelled_write_is_Cancelled(string write)
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await Write(Client(() => new HttpResponseMessage(HttpStatusCode.NoContent)), write, cts.Token);

        result.Error.Should().Be(RepositoriesApiClient.Cancelled);
    }

    /// <summary>
    ///     Each write goes to its endpoint with its method and body, and a 2xx answer is a success.
    ///     Red: invert the IsSuccessStatusCode check in the write helper; every write fails.
    ///     Red: send Update as a POST; the recorded method is POST.
    ///     Red: drop "/test-connection" from TestConnectionAsync; the recorded path differs.
    ///     Red: send Create's body as an empty object; the body has no name.
    ///     Red: send Update's body as an empty object; the body has no isActive.
    /// </summary>
    [Fact]
    public async Task Each_write_reaches_its_endpoint()
    {
        var id = Guid.NewGuid();
        var handler = new RecordingHandler()
            .On("/api/repositories", () => Json(HttpStatusCode.Created, "{}"))
            .On($"/api/repositories/{id}", () => new HttpResponseMessage(HttpStatusCode.NoContent))
            .On($"/api/repositories/{id}/test-connection", () => Json(HttpStatusCode.OK, "{\"success\":true}"));
        var client = new RepositoriesApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost") });

        var results = new[]
        {
            await client.CreateAsync(Create),
            await client.UpdateAsync(id, Update),
            await client.DeleteAsync(id),
            await client.TestConnectionAsync(id),
        };

        results.Should().AllSatisfy(r => r.IsSuccess.Should().BeTrue());
        handler.Requests.Select(r => (r.Method.Method, r.PathAndQuery)).Should().Equal(
            ("POST", "/api/repositories"),
            ("PUT", $"/api/repositories/{id}"),
            ("DELETE", $"/api/repositories/{id}"),
            ("POST", $"/api/repositories/{id}/test-connection"));
        handler.Requests[0].Body.Should().Contain("\"name\":\"atlas\"");
        handler.Requests[1].Body.Should().Contain("\"isActive\":true");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return respond(request, cancellationToken);
        }
    }

    /// <summary>
    ///     The resilience handler's own timeout is the read's timeout, and an open circuit is the reason the circuit
    ///     gives: neither escapes as an exception. In production the 60 s total timeout fires before HttpClient's 100 s.
    ///     Red: drop the TimeoutRejectedException catch in GetAllAsync; the read throws.
    ///     Red: drop ExecutionRejectedException from the read's message filter; the open circuit throws.
    /// </summary>
    [Fact]
    public async Task A_resilience_rejection_on_the_read_is_a_failure()
    {
        var timedOut = await Client(HttpResponseMessage () => throw new TimeoutRejectedException()).GetAllAsync(CancellationToken.None);
        var broken = await Client(HttpResponseMessage () => throw new BrokenCircuitException("The circuit is now open."))
            .GetAllAsync(CancellationToken.None);

        timedOut.Error.Should().Be(RepositoriesApiClient.TimedOut);
        broken.Error.Should().Be("The circuit is now open.");
    }

    /// <summary>
    ///     A write the resilience handler rejects got no answer, so the page says only what failed, as its catch-all did.
    ///     Red: drop ExecutionRejectedException from the write filter; the timeout throws.
    /// </summary>
    [Theory]
    [MemberData(nameof(Writes))]
    public async Task A_resilience_rejection_on_a_write_is_no_answer(string write)
    {
        var timedOut = await Write(Client(HttpResponseMessage () => throw new TimeoutRejectedException()), write);
        var broken = await Write(Client(HttpResponseMessage () => throw new BrokenCircuitException()), write);

        timedOut.Error.Should().Be(RepositoriesApiClient.NoAnswer);
        broken.Error.Should().Be(RepositoriesApiClient.NoAnswer);
    }
}
