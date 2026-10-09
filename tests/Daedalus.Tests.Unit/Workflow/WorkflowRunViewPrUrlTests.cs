using Daedalus.Agents.Workflow;
using Daedalus.Api.Controllers;

using Microsoft.AspNetCore.Mvc;
using Thalos.Workflow;
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
        var controller = new WorkflowRunsController(new WorkflowRunGateway(store, history, RecordStoreScopes.For(records), TimeProvider.System));

        var response = await controller.Get(run.Id, CancellationToken.None);

        return response.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<WorkflowRunView>().Subject;
    }

    /// <summary>
    ///     Red: reading the value with <c>new Uri(text, UriKind.Absolute)</c>, as before, throws for each of these, which
    ///     fails the request; accepting any absolute URI shows the <c>javascript:</c> and <c>file:</c> rows as links.
    /// </summary>
    [Theory]
    [InlineData("not a url")]
    [InlineData("pull/7")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/data/pr/7")]
    public async Task A_stored_pr_url_that_is_not_an_http_link_is_reported_as_text(string stored)
    {
        var view = await ViewOf(stored);

        view.PrUrl.Should().BeNull("only an absolute http or https URL is shown as the run's pull request");
        view.UnreadablePullRequestLink.Should().Be(stored);
    }

    /// <summary>The ordinary case still reads as a link. Red: reporting every value as unreadable.</summary>
    [Fact]
    public async Task A_stored_https_pr_url_is_the_link()
    {
        var view = await ViewOf("https://github.com/o/r/pull/7");

        view.PrUrl.Should().Be(new Uri("https://github.com/o/r/pull/7"));
        view.UnreadablePullRequestLink.Should().BeNull();
    }

    /// <summary>A run with no <c>pr_url</c> has neither. Red: reporting an absent value as unreadable text.</summary>
    [Fact]
    public async Task A_run_with_no_pr_url_has_neither()
    {
        var view = await ViewOf(null);

        view.PrUrl.Should().BeNull();
        view.UnreadablePullRequestLink.Should().BeNull();
    }
}
