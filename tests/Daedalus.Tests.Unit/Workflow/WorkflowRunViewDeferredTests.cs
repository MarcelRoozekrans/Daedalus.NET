using Daedalus.Agents;
using Daedalus.Agents.Workflow;
using Daedalus.Api.Controllers;
using Daedalus.Application.DTOs.WorkflowRuns;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos.Workflow;
using Thalos.Workspaces;
using Task = System.Threading.Tasks.Task;
using WorkflowRunRecord = Daedalus.Domain.Entities.WorkflowRunRecord;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>The deferred findings a run view shows at the gate, and how a drop record that cannot be read is reported.</summary>
public sealed class WorkflowRunViewDeferredTests
{
    private static readonly Guid RunId = Guid.NewGuid();

    private static WorkflowRunRecord Record(string kind, string payload) =>
        WorkflowRunRecord.Create(RunId, 4, "gate", kind, "p", null, payload, DateTime.UtcNow).Value;

    private const string OneDeferred =
        """{ "lens": "correctness", "verdict": "approved", "checked": ["x"], "findings": [], "deferred": [{"file":"a.cs","line":1,"title":"t","scenario":"s","reason":"blocked"}] }""";

    private static Task<WorkflowRunView> ViewWith(params WorkflowRunRecord[] extra) => ViewOf(OneDeferred, prUrl: null, extra);

    private static Task<WorkflowRunView> ViewOf(string evidencePayload, string? prUrl, params WorkflowRunRecord[] extra) =>
        ViewOf(evidencePayload, prUrl, workspaces: null, extra);

    /// <param name="evidencePayload">The review evidence record's payload.</param>
    /// <param name="prUrl">The run's pull request link, or none.</param>
    /// <param name="workspaces">The workspace provider, or <see langword="null"/> for one that names the allow-listed gizmos.</param>
    /// <param name="extra">More records of the run.</param>
    private static async Task<WorkflowRunView> ViewOf(
        string evidencePayload, string? prUrl, IRunWorkspaceProvider? workspaces, WorkflowRunRecord[] extra)
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
            Variables = prUrl is null
                ? new Dictionary<string, object?>(StringComparer.Ordinal)
                : new Dictionary<string, object?>(StringComparer.Ordinal) { [ReviewHandoff.PrUrlKey] = prUrl },
        };
        var store = Substitute.For<IWorkflowStore>();
        store.FindAsync(run.Id, Arg.Any<CancellationToken>()).Returns(new ValueTask<WorkflowRun?>(run));
        var history = Substitute.For<IWorkflowRunHistory>();
        history.ListEventsAsync(run.Id, Arg.Any<CancellationToken>()).Returns(new ValueTask<IReadOnlyList<WorkflowRunEvent>>([]));
        var evidence = Record(WorkflowRunRecord.ReviewEvidenceKind, evidencePayload);
        var records = Substitute.For<IWorkflowRunRecordStore>();
        records.ListAsync(run.Id, Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var kind = call.ArgAt<string?>(1);
            IReadOnlyList<WorkflowRunRecord> all = [evidence, .. extra];
            return new ValueTask<IReadOnlyList<WorkflowRunRecord>>([.. all.Where(r => kind is null || string.Equals(r.Kind, kind, StringComparison.Ordinal))]);
        });
        // The run's workspace names the allow-listed repository; its own remote is the sandbox's, not github.com, so
        // only the configured entry, found by name, can say where the repository lives.
        if (workspaces is null)
        {
            workspaces = Substitute.For<IRunWorkspaceProvider>();
            workspaces.FindAsync(run.Id, Arg.Any<CancellationToken>()).Returns(new ValueTask<RunWorkspace?>(
                new RunWorkspace(run.Id, "gizmos", "sandbox://gizmos", "main", $"manufacture/{run.Id}", "sandbox://run", null)));
        }

        var config = new WorkflowConfig();
        config.Repositories.Add(new RepositoryConfig { Name = "gizmos", Remote = "https://github.com/acme/gizmos.git" });
        var controller = new WorkflowRunsController(new WorkflowRunGateway(
            store, history, RecordStoreScopes.For(records), workspaces, config, TimeProvider.System, NullLogger<WorkflowRunGateway>.Instance));

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

    /// <summary>
    ///     Once the run has a pull request, a deferred finding's existing issue links into that repository, where
    ///     filing resolves it, even when the workspace names another; a finding with no issue has no link.
    ///     Red: return null for ExistingIssueUrl in ToDeferredView. Red: build the link without the issue number.
    ///     Red: link an issue-less finding to the repository anyway. Red: prefer the workspace repository.
    /// </summary>
    [Fact]
    public async Task An_existing_issue_links_into_the_pull_requests_repository()
    {
        const string Evidence =
            """{ "lens": "correctness", "verdict": "approved", "checked": ["x"], "findings": [], "deferred": [{"file":"a.cs","line":1,"title":"t","scenario":"s","reason":"blocked","existingIssue":42},{"file":"b.cs","line":2,"title":"u","scenario":"s","reason":"blocked"}] }""";

        var view = await ViewOf(Evidence, "https://github.com/acme/widgets/pull/17");

        view.DeferredFindings.Select(d => d.ExistingIssueUrl).Should().Equal(new Uri("https://github.com/acme/widgets/issues/42"), null);
    }

    /// <summary>
    ///     At the gate the run has no pull request yet, since publish runs after it, so the existing issue links into
    ///     the allow-listed repository the run's workspace names.
    ///     Red: drop the workspace fallback in ToViewAsync. Red: read the repository from the workspace's own remote.
    /// </summary>
    [Fact]
    public async Task At_the_gate_an_existing_issue_links_into_the_workspace_repository()
    {
        const string Evidence =
            """{ "lens": "correctness", "verdict": "approved", "checked": ["x"], "findings": [], "deferred": [{"file":"a.cs","line":1,"title":"t","scenario":"s","reason":"blocked","existingIssue":42}] }""";

        var view = await ViewOf(Evidence, prUrl: null);

        view.DeferredFindings.Should().ContainSingle().Which.ExistingIssueUrl.Should().Be(new Uri("https://github.com/acme/gizmos/issues/42"));
    }

    /// <summary>
    ///     A view whose deferred findings carry no issue number has nothing to link, so it never looks the workspace up.
    ///     Red: look the repository up whenever the run has no pull request.
    /// </summary>
    [Fact]
    public async Task A_view_with_no_issue_number_to_link_looks_no_workspace_up()
    {
        var workspaces = Substitute.For<IRunWorkspaceProvider>();

        var view = await ViewOf(OneDeferred, prUrl: null, workspaces, []);

        view.DeferredFindings.Should().ContainSingle().Which.ExistingIssueUrl.Should().BeNull();
        await workspaces.DidNotReceive().FindAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     A workspace lookup that throws leaves the issue number as text, with no link, and the view still answers.
    ///     Red: let the lookup's exception escape FindRepositoryAsync: the view throws instead of answering.
    /// </summary>
    [Fact]
    public async Task A_failed_workspace_lookup_leaves_the_issue_number_without_a_link()
    {
        const string Evidence =
            """{ "lens": "correctness", "verdict": "approved", "checked": ["x"], "findings": [], "deferred": [{"file":"a.cs","line":1,"title":"t","scenario":"s","reason":"blocked","existingIssue":42}] }""";
        var workspaces = Substitute.For<IRunWorkspaceProvider>();
        workspaces.FindAsync(RunId, Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromException<RunWorkspace?>(new InvalidOperationException("The workspace store is down.")));

        var view = await ViewOf(Evidence, prUrl: null, workspaces, []);

        var finding = view.DeferredFindings.Should().ContainSingle().Subject;
        finding.ExistingIssue.Should().Be(42);
        finding.ExistingIssueUrl.Should().BeNull();
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

    /// <summary>
    ///     Filed findings list by lens and number, with the summary last, whatever order the records are in. Red: return
    ///     the records in dictionary order; the summary and correctness-10 come out ahead.
    /// </summary>
    [Fact]
    public async Task Filed_findings_are_ordered_by_lens_and_number_with_the_summary_last()
    {
        FiledFinding Filed(string id, int issue) => new(id, FindingRecords.CreatedMode, issue, new Uri($"https://github.com/o/r/issues/{issue}"));

        var view = await ViewWith(
            Record(WorkflowRunRecord.FindingFiledKind, FindingRecords.FiledPayload(Filed(FindingRecords.SummaryId, 7))),
            Record(WorkflowRunRecord.FindingFiledKind, FindingRecords.FiledPayload(Filed("correctness-10", 12))),
            Record(WorkflowRunRecord.FindingFiledKind, FindingRecords.FiledPayload(Filed("security-1", 13))),
            Record(WorkflowRunRecord.FindingFiledKind, FindingRecords.FiledPayload(Filed("correctness-2", 11))));

        view.FiledFindings.Select(f => f.FindingId).Should().Equal("correctness-2", "correctness-10", "security-1", FindingRecords.SummaryId);
    }
}
