using System.Net;
using System.Text;
using Daedalus.Web.Services;

namespace Daedalus.Tests.Unit.Web;

/// <summary><c>ManufactureTaskAsync</c> turns every expected failure into a Result and never throws.</summary>
public sealed class ApiClientManufactureTests
{
    private static ApiClient Client(Func<HttpResponseMessage> respond) =>
        new(new HttpClient(new StubHandler(respond)) { BaseAddress = new Uri("http://localhost") });

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>Red: drop <c>JsonException</c> from the catch in ReadProblemDetailAsync; the call throws.</summary>
    [Fact]
    public async Task A_502_with_an_html_body_is_a_failure_not_an_exception()
    {
        var client = Client(() => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            ReasonPhrase = "Bad Gateway",
            Content = new StringContent("<html>bad gateway</html>", Encoding.UTF8, "text/html"),
        });

        var result = await client.ManufactureTaskAsync(Guid.NewGuid(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("502");
    }

    /// <summary>
    ///     An unsupported charset makes the JSON read throw InvalidOperationException, not JsonException.
    ///     Red: revert the catch in ReadProblemDetailAsync; the call throws.
    /// </summary>
    [Fact]
    public async Task A_429_with_an_unsupported_charset_is_a_failure_not_an_exception()
    {
        var client = Client(() =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)429) { Content = new ByteArrayContent("{}"u8.ToArray()) };
            response.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json; charset=bogus-9");
            return response;
        });

        var result = await client.ManufactureTaskAsync(Guid.NewGuid(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("429");
    }

    /// <summary>Red: stop reading the problem detail; the error becomes the status line.</summary>
    [Fact]
    public async Task A_409_problem_with_a_run_id_reports_its_detail()
    {
        var client = Client(() => Json(HttpStatusCode.Conflict,
            $$"""{"status":409,"detail":"A run is already live.","runId":"{{Guid.NewGuid()}}"}"""));

        var result = await client.ManufactureTaskAsync(Guid.NewGuid(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("A run is already live.");
    }

    /// <summary>Red: stop reading the problem detail; the error becomes the status line.</summary>
    [Fact]
    public async Task A_503_problem_reports_its_detail()
    {
        var client = Client(() => Json(HttpStatusCode.ServiceUnavailable,
            """{"status":503,"detail":"The workflow engine is disabled."}"""));

        var result = await client.ManufactureTaskAsync(Guid.NewGuid(), CancellationToken.None);

        result.Error.Should().Be("The workflow engine is disabled.");
    }

    /// <summary>Red: remove the success-path guard; a non-JSON 201 throws.</summary>
    [Fact]
    public async Task A_success_with_a_non_json_body_is_a_failure_not_an_exception()
    {
        var client = Client(() => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("ok", Encoding.UTF8, "text/plain"),
        });

        var result = await client.ManufactureTaskAsync(Guid.NewGuid(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
    }

    /// <summary>Red: remove the OperationCanceledException catch; the call throws.</summary>
    [Fact]
    public async Task A_cancelled_request_is_a_failure_not_an_exception()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var client = Client(() => Json(HttpStatusCode.Created, $$"""{"runId":"{{Guid.NewGuid()}}"}"""));

        var result = await client.ManufactureTaskAsync(Guid.NewGuid(), cts.Token);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Request was cancelled");
    }

    /// <summary>Red: break the success mapping; the run id is lost.</summary>
    [Fact]
    public async Task A_started_run_returns_its_id()
    {
        var runId = Guid.NewGuid();
        var client = Client(() => Json(HttpStatusCode.Created, $$"""{"runId":"{{runId}}"}"""));

        var result = await client.ManufactureTaskAsync(Guid.NewGuid(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.RunId.Should().Be(runId);
    }

    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond());
        }
    }
}
