using Daedalus.Agents;
using Daedalus.Agents.Workflow;
using Daedalus.Api.Endpoints;
using Daedalus.Application.DTOs.WorkflowRuns;
using Daedalus.Tests.Shared;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos.Workflow;
using Thalos.Workspaces;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Final review I1: <c>GET /api/workflow-runs/{id}</c> reads the run's <c>pr_url</c> as data. A stored value that is
///     not an absolute http or https URL is reported as text beside a null link, never as a 500 at the gate.
/// </summary>
public sealed class WorkflowRunViewPrUrlTests
{
    private static async Task<WorkflowRunView> ViewOf(object? prUrl)
    {
        var run = new WorkflowRun
        {
            Id = Guid.NewGuid(),
            Process = "manufacture",
            ProcessVersion = 6,
            CurrentNode = "gate",
            CurrentSeq = 4,
            Status = WorkflowStatus.Awaiting,
            Visits = new Dictionary<string, int>(StringComparer.Ordinal),
            Variables = new Dictionary<string, object?>(StringComparer.Ordinal) { [ReviewHandoff.PrUrlKey] = prUrl },
        };
        var store = Substitute.For<IWorkflowStore>();
        store.FindAsync(run.Id, Arg.Any<CancellationToken>()).Returns(new ValueTask<WorkflowRun?>(run));
        var history = Substitute.For<IWorkflowRunHistory>();
        history.ListEventsAsync(run.Id, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<WorkflowRunEvent>>([]));
        var records = Substitute.For<IWorkflowRunRecordStore>();
        records.ListAsync(run.Id, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<Daedalus.Domain.Entities.WorkflowRunRecord>>([]));
        await using var request = WorkflowRunsRequest.For(new WorkflowRunGateway(store, history, RecordStoreScopes.For(records), Substitute.For<IRunWorkspaceProvider>(), new WorkflowConfig(), TimeProvider.System, NullLogger<WorkflowRunGateway>.Instance));

        var response = await WorkflowRunsEndpoints.Get(run.Id, request.Context, CancellationToken.None);

        // Red: run is null || run.Id == id in place of run is null in WorkflowRunsEndpoints.Get; the found run
        //      answers 404.
        return response.Result.Should().BeOfType<Ok<WorkflowRunView>>().Which.Value!;
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("pull/7")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/data/pr/7")]
    public async Task A_stored_pr_url_that_is_not_an_http_link_is_reported_as_text(string stored)
    {
        // Red, per row: for "not a url" and "pull/7", new Uri(text, UriKind.Absolute) in place of Uri.TryCreate in
        //      PullRequestLink.Read, which throws for them and fails the request; for the javascript: and file: rows,
        //      the http and https scheme test dropped from PullRequestLink.Read, so they read as links.
        var view = await ViewOf(stored);

        view.PrUrl.Should().BeNull("only an absolute http or https URL is shown as the run's pull request");
        // Red: (null, null) in place of (null, text) for an unreadable value in PullRequestLink.Read.
        view.UnreadablePullRequestLink.Should().Be(stored);
    }

    /// <summary>The ordinary case still reads as a link.</summary>
    [Fact]
    public async Task A_stored_https_pr_url_is_the_link()
    {
        var view = await ViewOf("https://github.com/o/r/pull/7");

        // Red: (null, link.OriginalString) in place of (link, null) for a link in PullRequestLink.Read; the link is
        //      reported as unreadable text.
        view.PrUrl.Should().Be(new Uri("https://github.com/o/r/pull/7"));
        // Red: (link, text) in place of (link, null) for a link in PullRequestLink.Read.
        view.UnreadablePullRequestLink.Should().BeNull();
    }

    /// <summary>A run with no <c>pr_url</c> has neither.</summary>
    [Fact]
    public async Task A_run_with_no_pr_url_has_neither()
    {
        var view = await ViewOf(null);

        // Red: a relative Uri of ReviewHandoff.PrUrlKey in place of null as the link for an absent value in
        //      PullRequestLink.Read.
        view.PrUrl.Should().BeNull();
        // Red: an empty string in place of null as the unreadable text for an absent value in PullRequestLink.Read.
        view.UnreadablePullRequestLink.Should().BeNull();
    }
}
