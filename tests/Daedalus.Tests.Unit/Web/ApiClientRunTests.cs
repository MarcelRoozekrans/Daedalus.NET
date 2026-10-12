using System.Net;
using Daedalus.Web.State.Runs;

namespace Daedalus.Tests.Unit.Web;

/// <summary>The run methods of <see cref="Daedalus.Web.Services.ApiClient"/>: the exact requests, and every expected failure as a Result.</summary>
public sealed class ApiClientRunTests
{
    /// <summary>
    ///     Approve posts the gate's signal, the dropped ids and the apply flag to the run's resume route.
    ///     Red: send "approve" as the signal. Red: drop DropFindings from the body. Red: post to /cancel.
    ///     Red: send the approval as a PUT.
    /// </summary>
    [Fact]
    public async Task Approve_sends_the_gate_signal_and_the_drops()
    {
        var id = Guid.NewGuid();
        var handler = new RecordingHandler().On($"/api/workflow-runs/{id}/resume", () => new HttpResponseMessage(HttpStatusCode.Accepted));

        await handler.Client().ApproveRunAsync(id, ["mechanism-1"], applyStandingInstructions: true, CancellationToken.None);

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.PathAndQuery.Should().Be($"/api/workflow-runs/{id}/resume");
        request.Body.Should().Be(
            """{"signal":"human_approval","payload":null,"applyStandingInstructions":true,"dropFindings":["mechanism-1"]}""");
    }

    /// <summary>
    ///     A 404 for a run is a success with no run; a 409 with a ProblemDetails is a failure with its detail.
    ///     Red: treat 404 as a failure. Red: report "API error" instead of the detail.
    /// </summary>
    [Fact]
    public async Task A_missing_run_is_null_and_a_conflict_carries_its_reason()
    {
        var missing = Guid.NewGuid();
        var conflicted = Guid.NewGuid();
        var handler = new RecordingHandler()
            .On($"/api/workflow-runs/{missing}", () => new HttpResponseMessage(HttpStatusCode.NotFound))
            .OnJson($"/api/workflow-runs/{conflicted}", """{"status":409,"detail":"The run moved on."}""", HttpStatusCode.Conflict);
        var client = handler.Client();

        var none = await client.GetRunAsync(missing, CancellationToken.None);
        var refused = await client.GetRunAsync(conflicted, CancellationToken.None);

        // Value throws on a failed Result and a successful one carries no error, so each line pins the outcome too.
        none.Value.Should().BeNull();
        refused.Error.Should().Be("The run moved on.");
    }

    /// <summary>
    ///     The list query string carries status, process, after and take only when set, escaped.
    ///     Red: always send status=. Red: do not escape after.
    /// </summary>
    [Fact]
    public async Task The_list_query_carries_only_what_is_set()
    {
        var handler = new RecordingHandler();
        var client = handler.Client();

        await client.GetRunsAsync(RunListQuery.First, CancellationToken.None);
        await client.GetRunsAsync(new RunListQuery("Awaiting", "manufacture", "x+y/z="), CancellationToken.None);
        await client.GetRunsAsync(new RunListQuery("Awaiting", null, null, Take: 100), CancellationToken.None);

        handler.Paths.Should().Equal(
            "/api/workflow-runs?take=15",
            "/api/workflow-runs?take=15&status=Awaiting&process=manufacture&after=x%2By%2Fz%3D",
            "/api/workflow-runs?take=100&status=Awaiting");
    }
}
