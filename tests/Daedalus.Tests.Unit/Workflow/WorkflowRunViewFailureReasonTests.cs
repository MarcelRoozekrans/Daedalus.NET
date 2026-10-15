using Daedalus.Agents;
using Daedalus.Agents.Workflow;
using Daedalus.Api.Controllers;
using Daedalus.Api.Services;
using Daedalus.Application.DTOs.WorkflowRuns;
using Daedalus.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos;
using Thalos.Workflow;
using Thalos.Workspaces;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>The run view reads the rejecting lens off the latest review-evidence visit for a run the review limit stopped.</summary>
public sealed class WorkflowRunViewFailureReasonTests
{
    private static readonly Guid RunId = Guid.NewGuid();

    private static WorkflowRunEvent Event(long seq, string kind, string? from, string to, string status, string? outcome) =>
        new(seq, kind, from, to, status, outcome, null, null, DateTimeOffset.UnixEpoch, null);

    private static WorkflowRunRecord Evidence(long seq, string lens, string verdict) =>
        WorkflowRunRecord.Create(
            RunId, seq, "review", WorkflowRunRecord.ReviewEvidenceKind, "p", null,
            $$"""{ "lens": "{{lens}}", "verdict": "{{verdict}}", "checked": ["x"], "findings": [], "deferred": [] }""", DateTime.UtcNow).Value;

    private static WorkflowRun FailedRun() => new()
    {
        Id = RunId,
        Process = "manufacture",
        ProcessVersion = 6,
        CurrentNode = "adjudicate",
        CurrentSeq = 9,
        Status = WorkflowStatus.Failed,
        Visits = new Dictionary<string, int>(StringComparer.Ordinal),
        Variables = new Dictionary<string, object?>(StringComparer.Ordinal),
    };

    private static WorkflowRunsController ControllerFor(WorkflowRun run, IReadOnlyList<WorkflowRunEvent> events, WorkflowRunRecord[] evidence)
    {
        var store = Substitute.For<IWorkflowStore>();
        store.FindAsync(run.Id, Arg.Any<CancellationToken>()).Returns(new ValueTask<WorkflowRun?>(run));
        var history = Substitute.For<IWorkflowRunHistory>();
        history.ListEventsAsync(run.Id, Arg.Any<CancellationToken>()).Returns(new ValueTask<IReadOnlyList<WorkflowRunEvent>>(events));
        var records = Substitute.For<IWorkflowRunRecordStore>();
        records.ListAsync(run.Id, Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var kind = call.ArgAt<string?>(1);
            return new ValueTask<IReadOnlyList<WorkflowRunRecord>>(
                [.. evidence.Where(r => kind is null || string.Equals(r.Kind, kind, StringComparison.Ordinal))]);
        });
        return new WorkflowRunsController(new WorkflowRunGateway(
            store, history, RecordStoreScopes.For(records), Substitute.For<IRunWorkspaceProvider>(), new WorkflowConfig(),
            TimeProvider.System, NullLogger<WorkflowRunGateway>.Instance));
    }

    private static async Task<WorkflowRunView> ViewAsync(IReadOnlyList<WorkflowRunEvent> events, params WorkflowRunRecord[] evidence)
    {
        var run = FailedRun();
        var response = await ControllerFor(run, events, evidence).Get(run.Id, CancellationToken.None);

        return response.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<WorkflowRunView>().Subject;
    }

    private static async Task<WorkflowRunListItem> ListItemAsync(IReadOnlyList<WorkflowRunEvent> events, params WorkflowRunRecord[] evidence)
    {
        var run = FailedRun();
        var query = Substitute.For<IWorkflowRunQuery>();
        query.ListAsync(Arg.Any<WorkflowRunFilter>(), Arg.Any<CancellationToken>()).Returns(new WorkflowRunPage(
            [new WorkflowRunSummary { Id = run.Id, Process = run.Process, ProcessVersion = run.ProcessVersion, Status = run.Status, CurrentNode = run.CurrentNode, CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch }],
            null));
        var tasks = Substitute.For<ITaskQueryService>();
        tasks.GetRunLinksAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TaskRunLink>());

        var response = await ControllerFor(run, events, evidence).List(null, null, null, null, null, null, query, tasks, CancellationToken.None);

        return response.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<WorkflowRunList>().Which.Runs.Should().ContainSingle().Subject;
    }

    /// <summary>Five review visits, then the cap and the terminal step, as the engine records them for manufacture.</summary>
    private static WorkflowRunEvent[] CappedLog()
    {
        var events = new List<WorkflowRunEvent> { Event(0, "Entered", null, "implement", "Running", null) };
        for (var visit = 0; visit < 5; visit++)
        {
            events.Add(Event(events.Count, "Branched", "implement", "review", "Running", "changed"));
            events.Add(Event(events.Count, "Branched", "review", "implement", "Running", "rejected"));
        }

        events.Add(Event(events.Count, "CapExceeded", "implement", "adjudicate", "Running", "changed"));
        events.Add(Event(events.Count, "Completed", "adjudicate", "adjudicate", "Failed", null));
        return [.. events];
    }

    /// <summary>
    ///     The lens is the one that rejected the latest visit, not an earlier visit's.
    ///     Red: pass a null lens to Derive.
    /// </summary>
    [Fact]
    public async Task A_capped_run_names_the_lens_that_rejected_the_latest_visit()
    {
        var view = await ViewAsync(
            CappedLog(), Evidence(2, "mechanism", "rejected"), Evidence(6, "falsifiability", "rejected"));

        view.FailureReason.Should().Be("Review rejected the change 5 times, the review limit. Last rejecting lens: falsifiability.");
    }

    /// <summary>
    ///     A review-evidence record that cannot be read drops the lens sentence and nothing else.
    ///     Red: take a lens even when the review evidence failed to read.
    /// </summary>
    [Fact]
    public async Task Unreadable_evidence_leaves_the_lens_sentence_out()
    {
        var bad = WorkflowRunRecord.Create(
            RunId, 6, "review", WorkflowRunRecord.ReviewEvidenceKind, "p", null, """{ "nonsense": true }""", DateTime.UtcNow).Value;

        var view = await ViewAsync(CappedLog(), bad);

        view.FailureReason.Should().Be("Review rejected the change 5 times, the review limit.");
    }

    /// <summary>
    ///     The list item of a capped run carries the same reason as its view, lens sentence included.
    ///     Red: pass a null lens to Derive in ToListItemAsync. Red: read the lens only for the view path.
    /// </summary>
    [Fact]
    public async Task A_capped_list_item_carries_the_lens_sentence()
    {
        var item = await ListItemAsync(CappedLog(), Evidence(2, "mechanism", "rejected"), Evidence(6, "falsifiability", "rejected"));

        item.FailureReason.Should().Be("Review rejected the change 5 times, the review limit. Last rejecting lens: falsifiability.");
    }

    /// <summary>
    ///     A list item whose evidence cannot be read keeps the first sentence.
    ///     Red: take a lens even when the review evidence failed to read.
    /// </summary>
    [Fact]
    public async Task An_unreadable_evidence_list_item_leaves_the_lens_sentence_out()
    {
        var bad = WorkflowRunRecord.Create(
            RunId, 6, "review", WorkflowRunRecord.ReviewEvidenceKind, "p", null, """{ "nonsense": true }""", DateTime.UtcNow).Value;

        var item = await ListItemAsync(CappedLog(), bad);

        item.FailureReason.Should().Be("Review rejected the change 5 times, the review limit.");
    }
}
