using Daedalus.Agents.Workflow;
using Daedalus.Api.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos.Workflow;
using Task = System.Threading.Tasks.Task;
using WorkflowRunRecord = Daedalus.Domain.Entities.WorkflowRunRecord;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>The deferred findings a run view shows at the gate, and how a drop record that cannot be read is reported.</summary>
public sealed class WorkflowRunViewDeferredTests
{
    private static readonly Guid RunId = Guid.NewGuid();

    private static WorkflowRunRecord Record(string kind, string payload) =>
        WorkflowRunRecord.Create(RunId, 4, "gate", kind, "p", null, payload, DateTime.UtcNow).Value;

    private static async Task<WorkflowRunView> ViewWith(params WorkflowRunRecord[] extra)
    {
        var run = new WorkflowRun
        {
            Id = RunId,
            Process = "manufacture",
            ProcessVersion = 6,
            CurrentNode = "gate",
            CurrentSeq = 4,
            Status = WorkflowStatus.Awaiting,
            Visits = new Dictionary<string, int>(StringComparer.Ordinal),
        };
        var store = Substitute.For<IWorkflowStore>();
        store.FindAsync(run.Id, Arg.Any<CancellationToken>()).Returns(new ValueTask<WorkflowRun?>(run));
        var history = Substitute.For<IWorkflowRunHistory>();
        history.ListEventsAsync(run.Id, Arg.Any<CancellationToken>()).Returns(new ValueTask<IReadOnlyList<WorkflowRunEvent>>([]));
        var evidence = Record(
            WorkflowRunRecord.ReviewEvidenceKind,
            """{ "lens": "correctness", "verdict": "approved", "checked": ["x"], "findings": [], "deferred": [{"file":"a.cs","line":1,"title":"t","scenario":"s","reason":"blocked"}] }""");
        var records = Substitute.For<IWorkflowRunRecordStore>();
        records.ListAsync(run.Id, Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var kind = call.ArgAt<string?>(1);
            IReadOnlyList<WorkflowRunRecord> all = [evidence, .. extra];
            return new ValueTask<IReadOnlyList<WorkflowRunRecord>>([.. all.Where(r => kind is null || r.Kind == kind)]);
        });
        var controller = new WorkflowRunsController(new WorkflowRunGateway(store, history, RecordStoreScopes.For(records), TimeProvider.System, NullLogger<WorkflowRunGateway>.Instance));

        var response = await controller.Get(run.Id, CancellationToken.None);

        return response.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<WorkflowRunView>().Subject;
    }

    /// <summary>
    ///     A corrupt drop record must not read as "nothing dropped" in silence. Red: ignore <c>dropped.Error</c>; the
    ///     error is null.
    /// </summary>
    [Fact]
    public async Task An_unreadable_drop_record_is_reported_on_the_view()
    {
        var view = await ViewWith(Record(WorkflowRunRecord.FindingsDroppedKind, """{ "dropped": "correctness-1" }"""));

        view.DeferredFindingsError.Should().Contain("findings-dropped record");
        view.DeferredFindings.Should().ContainSingle().Which.Dropped.Should().BeFalse();
    }

    /// <summary>Red: list only the drop records for the view; the voided drop still shows as dropped.</summary>
    [Fact]
    public async Task A_voided_drop_is_not_shown_as_dropped()
    {
        var view = await ViewWith(
            Record(WorkflowRunRecord.FindingsDroppedKind, FindingRecords.DroppedPayload(["correctness-1"], "admin", "a")),
            Record(WorkflowRunRecord.FindingsDropVoidedKind, FindingRecords.DropVoidedPayload("a")));

        view.DeferredFindingsError.Should().BeNull();
        view.DeferredFindings.Should().ContainSingle().Which.Dropped.Should().BeFalse();
    }

    /// <summary>Red: mark nothing as dropped; the finding is not flagged.</summary>
    [Fact]
    public async Task A_dropped_finding_is_marked_dropped()
    {
        var view = await ViewWith(Record(WorkflowRunRecord.FindingsDroppedKind, FindingRecords.DroppedPayload(["correctness-1"], "admin", "a")));

        view.DeferredFindings.Should().ContainSingle().Which.Dropped.Should().BeTrue();
    }

    /// <summary>Red: leave FiledFindings empty; the single finding is missing.</summary>
    [Fact]
    public async Task A_filed_finding_is_shown_with_its_issue_link()
    {
        var view = await ViewWith(Record(WorkflowRunRecord.FindingFiledKind,
            FindingRecords.FiledPayload(new FiledFinding("correctness-1", FindingRecords.CreatedMode, 21, new Uri("https://github.com/o/r/issues/21")))));

        view.FiledFindings.Should().ContainSingle().Which.Should().Be(new FiledFindingView("correctness-1", "created", 21, new Uri("https://github.com/o/r/issues/21")));
    }

    /// <summary>Red: ignore the filed read's error; the error is null.</summary>
    [Fact]
    public async Task An_unreadable_filed_record_is_reported_on_the_view()
    {
        var view = await ViewWith(Record(WorkflowRunRecord.FindingFiledKind, """{ "id": "correctness-1" }"""));

        view.DeferredFindingsError.Should().Contain("finding-filed record");
        view.FiledFindings.Should().BeEmpty();
    }
}
