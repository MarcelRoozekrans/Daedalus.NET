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
        await using var request = WorkflowRunsRequest.For(new WorkflowRunGateway(
            store, history, RecordStoreScopes.For(records), workspaces, config, TimeProvider.System, NullLogger<WorkflowRunGateway>.Instance));

        var response = await WorkflowRunsEndpoints.Get(run.Id, request.Context, CancellationToken.None);

        // Red: run is null || run.Id == id in place of run is null in WorkflowRunsEndpoints.Get; the found run
        //      answers 404.
        return response.Result.Should().BeOfType<Ok<WorkflowRunView>>().Which.Value!;
    }

    private static WorkflowRunRecord Selection(long seq, string node, string payload) =>
        WorkflowRunRecord.Create(RunId, seq, node, WorkflowRunRecord.NodeSelectionKind, "p", null, payload, DateTime.UtcNow).Value;

    /// <summary>
    ///     The view lists each node-selection record in seq order, with the model, level, kept family count, fallback and
    ///     confidence, and carries no tool name.
    /// </summary>
    [Fact]
    public async Task The_run_view_lists_recorded_node_selections()
    {
        var view = await ViewWith(
            Selection(2, "review", """{ "level": "high", "model": "claude-opus-5-5", "tool_filter": null, "kept_families": 5, "fallback": "timeout", "confidence": null }"""),
            Selection(1, "implement", """{ "level": "low", "model": "claude-haiku-5-5", "tool_filter": ["secret_tool__run"], "kept_families": 3, "fallback": null, "confidence": 0.8 }"""));

        // Red: an empty NodeSelections list in place of the readSelections one in WorkflowRunsEndpoints.ToViewAsync.
        // Red: the OrderBy on Seq dropped from readSelections in ToViewAsync; the records come back 2 then 1.
        // Red: JsonPropertyName "keptFamilies" in place of "kept_families" on NodeSelectionRecord.Payload; the kept
        //      family counts read 0.
        view.NodeSelections.Should().Equal(
            new NodeSelectionView(1, "implement", "claude-haiku-5-5", "low", 3, null, 0.8),
            new NodeSelectionView(2, "review", "claude-opus-5-5", "high", 5, "timeout", null));
        // Red: ReviewVisitsError set to reviewVisitsError, or else the selection records' payloads joined with ',',
        //      in ToViewAsync; the serialized view then carries the tool name.
        System.Text.Json.JsonSerializer.Serialize(view, System.Text.Json.JsonSerializerOptions.Web).Should().NotContain("secret_tool__run");
    }

    /// <summary>
    ///     A node-selection record that cannot be read is not dropped silently: the readable ones are listed and the error
    ///     names the seq of each unreadable one, as the deferred findings error does.
    /// </summary>
    [Fact]
    public async Task An_unreadable_node_selection_is_reported_not_dropped()
    {
        var view = await ViewWith(
            Selection(1, "implement", """{ "level": "low", "model": "claude-haiku-5-5", "tool_filter": null, "kept_families": 3, "fallback": null, "confidence": 0.8 }"""),
            Selection(2, "review", """{ "unexpected": true }"""),
            Selection(3, "review", """{ "level": "low", "model": " ", "tool_filter": null, "kept_families": 3, "fallback": null, "confidence": null }"""));

        // Red: the NodeSelections list in ToViewAsync kept only while unreadableSelections is empty; the readable one
        //      is missing.
        // Red: the IsNullOrWhiteSpace test of payload.Model dropped from NodeSelectionRecord.Parse; the blank-model
        //      record lists instead of erring.
        view.NodeSelections.Should().ContainSingle().Which.Seq.Should().Be(1);
        // Red: unreadableSelections.Count > 100 in place of > 0 for NodeSelectionsError in ToViewAsync; it is null.
        // Red: the View-is-null filter dropped from unreadableSelections in ToViewAsync; seq 1 is named too.
        view.NodeSelectionsError.Should().Be("2 node-selection record(s) could not be read, at seq 2, 3.");
    }

    [Fact]
    public async Task Readable_node_selections_have_no_error()
    {
        var view = await ViewWith(
            Selection(1, "implement", """{ "level": "low", "model": "m", "tool_filter": ["a__b"], "kept_families": 3, "fallback": null, "confidence": null }"""));

        // Red: an empty string in place of null for NodeSelectionsError when every record reads, in ToViewAsync.
        view.NodeSelectionsError.Should().BeNull();
    }

    /// <summary>
    ///     A run with no decisions, such as one with decisions off before this phase, has an empty list.
    /// </summary>
    [Fact]
    public async Task A_run_without_node_selections_lists_none()
    {
        var view = await ViewWith();

        // Red: a null NodeSelections list when readSelections is empty, in ToViewAsync.
        view.NodeSelections.Should().BeEmpty();
    }

    /// <summary>
    ///     A corrupt drop record must not read as "nothing dropped" in silence.
    /// </summary>
    [Fact]
    public async Task An_unreadable_drop_record_is_reported_on_the_view()
    {
        var view = await ViewWith(Record(WorkflowRunRecord.FindingsDroppedKind, """{ "dropped": "correctness-1" }"""));

        // Red: null in place of dropped.Error in the error join of ToViewAsync; the error is null.
        view.DeferredFindingsError.Should().Contain("findings-dropped record");
        // Red: droppedIds as every deferred finding's id when the drop record does not read, in ToViewAsync.
        view.DeferredFindings.Should().ContainSingle().Which.Dropped.Should().BeFalse();
    }

    [Fact]
    public async Task A_voided_drop_is_not_shown_as_dropped()
    {
        var view = await ViewWith(
            Record(WorkflowRunRecord.FindingsDroppedKind, FindingRecords.DroppedPayload(["correctness-1"], "admin", "a")),
            Record(WorkflowRunRecord.FindingsDropVoidedKind, FindingRecords.DropVoidedPayload("a")));

        // Red: the not-null filter dropped from the error join of ToViewAsync; the error is then the joined nulls.
        view.DeferredFindingsError.Should().BeNull();
        // Red: voids.Take(0) in place of voids in the ReadDropped call of ToViewAsync, so only the drop records are
        //      read; the voided drop still shows as dropped.
        view.DeferredFindings.Should().ContainSingle().Which.Dropped.Should().BeFalse();
    }

    [Fact]
    public async Task A_dropped_finding_is_marked_dropped()
    {
        var view = await ViewWith(Record(WorkflowRunRecord.FindingsDroppedKind, FindingRecords.DroppedPayload(["correctness-1"], "admin", "a")));

        // Red: dropped.Contains(d.Id) && d.Id.Length == 0 in ToDeferredView; nothing is marked dropped.
        view.DeferredFindings.Should().ContainSingle().Which.Dropped.Should().BeTrue();
    }

    /// <summary>
    ///     Once the run has a pull request, a deferred finding's existing issue links into that repository, where
    ///     filing resolves it, even when the workspace names another; a finding with no issue has no link.
    /// </summary>
    [Fact]
    public async Task An_existing_issue_links_into_the_pull_requests_repository()
    {
        const string Evidence =
            """{ "lens": "correctness", "verdict": "approved", "checked": ["x"], "findings": [], "deferred": [{"file":"a.cs","line":1,"title":"t","scenario":"s","reason":"blocked","existingIssue":42},{"file":"b.cs","line":2,"title":"u","scenario":"s","reason":"blocked"}] }""";

        var view = await ViewOf(Evidence, "https://github.com/acme/widgets/pull/17");

        // Red: an issue > 1000 test added to the link condition in ToDeferredView; no finding is linked.
        // Red: issueRepo.IssueUrl(1) in place of IssueUrl(issue) in ToDeferredView; the link loses its number.
        // Red: the link built from ExistingIssue ?? 0 whenever there is a repository, in ToDeferredView; the
        //      issue-less finding is linked too.
        // Red: issueRepo set to the workspace's repository, or else the pull request's, in ToViewAsync; the link
        //      goes to gizmos.
        view.DeferredFindings.Select(d => d.ExistingIssueUrl).Should().Equal(new Uri("https://github.com/acme/widgets/issues/42"), null);
    }

    /// <summary>
    ///     At the gate the run has no pull request yet, since publish runs after it, so the existing issue links into
    ///     the allow-listed repository the run's workspace names.
    /// </summary>
    [Fact]
    public async Task At_the_gate_an_existing_issue_links_into_the_workspace_repository()
    {
        const string Evidence =
            """{ "lens": "correctness", "verdict": "approved", "checked": ["x"], "findings": [], "deferred": [{"file":"a.cs","line":1,"title":"t","scenario":"s","reason":"blocked","existingIssue":42}] }""";

        var view = await ViewOf(Evidence, prUrl: null);

        // Red: a prUrl-is-not-null test added to the workspace fallback in ToViewAsync; there is no link.
        // Red: RepoRef.FromGitHubUrl(workspace.Remote) in place of configured.Remote in
        //      WorkflowRunGateway.FindRepositoryAsync; the sandbox remote gives no repository and no link.
        view.DeferredFindings.Should().ContainSingle().Which.ExistingIssueUrl.Should().Be(new Uri("https://github.com/acme/gizmos/issues/42"));
    }

    /// <summary>
    ///     A view whose deferred findings carry no issue number has nothing to link, so it never looks the workspace up.
    /// </summary>
    [Fact]
    public async Task A_view_with_no_issue_number_to_link_looks_no_workspace_up()
    {
        var workspaces = Substitute.For<IRunWorkspaceProvider>();

        var view = await ViewOf(OneDeferred, prUrl: null, workspaces, []);

        // Red: none at one site: a link needs both a repository and an issue number, and this view has neither; the
        //      issue-less mutation of the pull-request case above was applied here as well and leaves it null.
        view.DeferredFindings.Should().ContainSingle().Which.ExistingIssueUrl.Should().BeNull();
        // Red: the workspace fallback in ToViewAsync taken whenever there is no pull request, without its
        //      issue-number test; the workspace is looked up.
        await workspaces.DidNotReceive().FindAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     A workspace lookup that throws leaves the issue number as text, with no link, and the view still answers.
    /// </summary>
    [Fact]
    public async Task A_failed_workspace_lookup_leaves_the_issue_number_without_a_link()
    {
        const string Evidence =
            """{ "lens": "correctness", "verdict": "approved", "checked": ["x"], "findings": [], "deferred": [{"file":"a.cs","line":1,"title":"t","scenario":"s","reason":"blocked","existingIssue":42}] }""";
        var workspaces = Substitute.For<IRunWorkspaceProvider>();
        workspaces.FindAsync(RunId, Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromException<RunWorkspace?>(new InvalidOperationException("The workspace store is down.")));

        // Red: the catch in WorkflowRunGateway.FindRepositoryAsync filtered to OperationCanceledException only; the
        //      lookup's exception escapes and the view throws instead of answering.
        var view = await ViewOf(Evidence, prUrl: null, workspaces, []);

        var finding = view.DeferredFindings.Should().ContainSingle().Subject;
        // Red: d.Finding.ExistingIssue + 1 as the view's issue number in ToDeferredView.
        finding.ExistingIssue.Should().Be(42);
        // Red: the catch in FindRepositoryAsync answering RepoRef.FromGitHubUrl of the first configured repository's
        //      remote in place of null; the issue is linked.
        finding.ExistingIssueUrl.Should().BeNull();
    }

    [Fact]
    public async Task A_filed_finding_is_shown_with_its_issue_link()
    {
        var view = await ViewWith(Record(WorkflowRunRecord.FindingFiledKind,
            FindingRecords.FiledPayload(new FiledFinding("correctness-1", FindingRecords.CreatedMode, 21, new Uri("https://github.com/o/r/issues/21")))));

        // Red: filed.IsFailure in place of filed.IsSuccess for FiledFindings in ToViewAsync; the list is empty.
        view.FiledFindings.Should().ContainSingle().Which.Should().Be(new FiledFindingView("correctness-1", "created", 21, new Uri("https://github.com/o/r/issues/21")));
    }

    [Fact]
    public async Task An_unreadable_filed_record_is_reported_on_the_view()
    {
        var view = await ViewWith(Record(WorkflowRunRecord.FindingFiledKind, """{ "id": "correctness-1" }"""));

        // Red: null in place of filed.Error in the error join of ToViewAsync; the error is null.
        view.DeferredFindingsError.Should().Contain("finding-filed record");
        // Red: a placeholder FiledFindingView, ids "x" and a null! link, in place of the empty list for an unread
        //      filed record, in ToViewAsync.
        view.FiledFindings.Should().BeEmpty();
    }

    /// <summary>
    ///     Filed findings list by lens and number, with the summary last, whatever order the records are in.
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

        // Red: the finding ids' ordinal order ahead of FiledOrder, OrderBy then ThenBy, in place of
        //      Order(FiledOrder.Instance) in ToViewAsync; correctness-10 then comes before correctness-2.
        view.FiledFindings.Select(f => f.FindingId).Should().Equal("correctness-2", "correctness-10", "security-1", FindingRecords.SummaryId);
    }
}
