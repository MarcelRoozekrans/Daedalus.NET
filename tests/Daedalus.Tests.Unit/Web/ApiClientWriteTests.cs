using System.Net;
using System.Text;
using Daedalus.Web.Services;

namespace Daedalus.Tests.Unit.Web;

/// <summary>
///     The write helpers behind update, delete and the other writes turn every expected failure into a Result that carries
///     the server's reason, and never throw.
/// </summary>
public sealed class ApiClientWriteTests
{
    private const string LiveReason = "Task has a live run: run x is Running. Wait until it finishes, or cancel it first.";

    private static readonly UpdateTaskDto Edit = new("Title", null, null, null, null, null, null);

    private static ApiClient Client(Func<HttpResponseMessage> respond) =>
        new(new HttpClient(new StubHandler(respond)) { BaseAddress = new Uri("http://localhost") });

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage ErrorBody(HttpStatusCode code, string error) =>
        Json(code, System.Text.Json.JsonSerializer.Serialize(new { error }));

    /// <summary>Red: put <c>EnsureSuccessStatusCode</c> back in the PUT path; the error becomes the generic 409 text.</summary>
    [Fact]
    public async Task A_409_on_update_reports_the_live_run_reason()
    {
        var client = Client(() => ErrorBody(HttpStatusCode.Conflict, LiveReason));

        var result = await client.UpdateTaskAsync(Guid.NewGuid(), Edit, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain(LiveReason);
    }

    /// <summary>Red: put <c>EnsureSuccessStatusCode</c> back in the DELETE path; the error becomes the generic 409 text.</summary>
    [Fact]
    public async Task A_409_on_delete_reports_the_live_run_reason()
    {
        var client = Client(() => ErrorBody(HttpStatusCode.Conflict, LiveReason));

        var result = await client.DeleteTaskAsync(Guid.NewGuid(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain(LiveReason);
    }

    /// <summary>Red: stop reading the <c>error</c> field in ReadFailureReasonAsync; the error becomes the status line.</summary>
    [Fact]
    public async Task A_400_on_create_reports_the_error_field()
    {
        var client = Client(() => ErrorBody(HttpStatusCode.BadRequest, "Title is required."));

        var result = await client.CreateTaskAsync(new CreateTaskDto(Guid.NewGuid(), null, "T", "D", 1, null, 0, 1, "P", null, null), CancellationToken.None);

        result.Error.Should().Be("Title is required.");
    }

    /// <summary>Red: read the success body without the TryReadJsonAsync guard; the PUT throws a JsonException.</summary>
    [Fact]
    public async Task A_success_with_a_non_json_body_on_update_is_a_failure_not_an_exception()
    {
        var client = Client(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("ok", Encoding.UTF8, "text/plain"),
        });

        var result = await client.UpdateTaskAsync(Guid.NewGuid(), Edit, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
    }

    /// <summary>Red: remove the OperationCanceledException catch from the generic SendAsync; the PUT throws.</summary>
    [Fact]
    public async Task A_cancelled_update_is_a_failure_not_an_exception()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var client = Client(() => Json(HttpStatusCode.OK, "{}"));

        var result = await client.UpdateTaskAsync(Guid.NewGuid(), Edit, cts.Token);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Request was cancelled");
    }

    /// <summary>Red: remove the OperationCanceledException catch from the non-generic SendAsync; the DELETE throws.</summary>
    [Fact]
    public async Task A_cancelled_delete_is_a_failure_not_an_exception()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var client = Client(() => new HttpResponseMessage(HttpStatusCode.NoContent));

        var result = await client.DeleteTaskAsync(Guid.NewGuid(), cts.Token);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Request was cancelled");
    }

    /// <summary>Red: invert the IsSuccessStatusCode check in the non-generic SendAsync; the 204 becomes a failure.</summary>
    [Fact]
    public async Task A_204_on_delete_is_a_success()
    {
        var client = Client(() => new HttpResponseMessage(HttpStatusCode.NoContent));

        var result = await client.DeleteTaskAsync(Guid.NewGuid(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
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
