using System.Globalization;
using Daedalus.Agents.Workflow;
using Daedalus.Domain.Entities;
using Thalos;
using Thalos.Workflow;
using ZeroAlloc.Results;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Phase 2.8: every completed agent node with usage appends exactly one <c>node-usage</c> record, after the store has
///     accepted the completion. Cost analytics reads manufacture usage from these records.
/// </summary>
public sealed class NodeUsageRecordTests
{
    private const string ProcessName = "manufacture";

    private static readonly TurnUsage Usage = new(1_200, 80, "claude-x") { CacheReadTokens = 900, CacheWriteTokens = 150 };

    private readonly IWorkflowRunRecordStore _records = Substitute.For<IWorkflowRunRecordStore>();

    private static WorkflowRun RunAt(string node, int existingKeys = 0) => new()
    {
        Id = Guid.NewGuid(),
        Process = ProcessName,
        ProcessVersion = 4,
        CurrentNode = node,
        CurrentSeq = 3,
        Status = WorkflowStatus.Running,
        StartedBy = new RunPrincipal("a-dev", ["developer"]),
        Visits = new Dictionary<string, int>(StringComparer.Ordinal) { [node] = 1 },
        Variables = Enumerable.Range(0, existingKeys).ToDictionary(
            i => "standing" + i.ToString(CultureInfo.InvariantCulture), _ => (object?)"value", StringComparer.Ordinal),
    };

    private static IProcessDefinitionStore Definitions()
    {
        var definition = new ProcessDefinition
        {
            Name = ProcessName,
            Version = 4,
            StartNode = "implement",
            Nodes = new Dictionary<string, ProcessNode>(StringComparer.Ordinal)
            {
                ["implement"] = new() { Agent = "implementer", Skill = "manufacture-implement", Next = "publish" },
                ["review"] = new()
                {
                    Agent = "reviewer",
                    Skill = "manufacture-review",
                    Outcomes = ["approved", "rejected"],
                    Branch = new Dictionary<string, string>(StringComparer.Ordinal) { ["approved"] = "publish", ["rejected"] = "implement" },
                    Lenses = ["correctness"],
                },
                ["publish"] = new()
                {
                    Action = ReviewHandoff.PublishActionName,
                    Outcomes = ["published", "failed"],
                    Branch = new Dictionary<string, string>(StringComparer.Ordinal) { ["published"] = "done", ["failed"] = "done" },
                },
                ["done"] = new() { Terminal = "succeeded" },
            },
        };

        var store = Substitute.For<IProcessDefinitionStore>();
        store.GetAsync(ProcessName, 4, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result<ProcessDefinition>>(Result<ProcessDefinition>.Success(definition)));
        return store;
    }

    private static WorkflowTransition AnyTransition() =>
        new("done", WorkflowStatus.Running, awaitingSignal: null, WorkflowEventKind.Branched);

    private ReviewHandoffWorkflowStore Store(IWorkflowStore inner) =>
        new(inner, Definitions(), TestNodeUsage.Recorder(_records));

    /// <summary>
    ///     Red: delete the append after <c>Inner.CompleteNodeAsync</c>; nothing is received.
    ///     Red: record <c>run.CurrentSeq + 1</c> or the transition's target node; the seq or node assertion fails.
    ///     Red: write the cache counts swapped in the recorder; the payload assertion fails.
    /// </summary>
    [Fact]
    public async Task Completing_an_agent_node_with_usage_appends_one_node_usage_record()
    {
        var run = RunAt("implement");
        WorkflowRunRecord? appended = null;
        await _records.AppendAsync(Arg.Do<WorkflowRunRecord>(r => appended = r), Arg.Any<CancellationToken>());

        await Store(new RecordingWorkflowStore(run)).CompleteNodeAsync(
            run.Id, 3, AnyTransition(), new NodeResult("changed", new Dictionary<string, object?>(StringComparer.Ordinal)) { Usage = Usage }, CancellationToken.None);

        await _records.Received(1).AppendAsync(Arg.Any<WorkflowRunRecord>(), Arg.Any<CancellationToken>());
        appended!.Should().BeEquivalentTo(new
        {
            RunId = run.Id,
            Seq = 3L,
            Node = "implement",
            Kind = WorkflowRunRecord.NodeUsageKind,
            PrincipalId = NodeUsageRecorder.HostPrincipalId,
            StartedById = "a-dev",
        });
        NodeUsage.FromPayloadJson(appended.PayloadJson).Value.Should().Be(new NodeUsage("claude-x", 1_200, 80, 900, 150));
    }

    /// <summary>
    ///     A host action reads no turn, so its result carries no usage and no record is written.
    ///     Red: append whenever the run exists, with <c>result.Usage ?? default</c>.
    /// </summary>
    [Fact]
    public async Task A_host_action_completion_without_usage_appends_nothing()
    {
        var run = RunAt("publish");

        await Store(new RecordingWorkflowStore(run)).CompleteNodeAsync(
            run.Id, 3, AnyTransition(), new NodeResult("published", new Dictionary<string, object?>(StringComparer.Ordinal)), CancellationToken.None);

        await _records.DidNotReceiveWithAnyArgs().AppendAsync(default!, default);
    }

    /// <summary>
    ///     A node failed for the key cap was never completed, so it has no completion and no record.
    ///     Red: move the append above the key-cap check.
    /// </summary>
    [Fact]
    public async Task A_node_failed_for_the_key_cap_appends_nothing()
    {
        var run = RunAt("implement", existingKeys: 10);
        var inner = new RecordingWorkflowStore(run);
        var report = new Dictionary<string, object?>(StringComparer.Ordinal) { ["fresh0"] = "v", ["fresh1"] = "v" };

        await Store(inner).CompleteNodeAsync(run.Id, 3, AnyTransition(), new NodeResult("changed", report) { Usage = Usage }, CancellationToken.None);

        inner.FailureMessage.Should().NotBeNull("ten keys plus two fresh ones and the host's reservation pass the cap");
        await _records.DidNotReceiveWithAnyArgs().AppendAsync(default!, default);
    }

    /// <summary>
    ///     A completion the store rejects is not recorded either. Red: append before <c>Inner.CompleteNodeAsync</c>.
    /// </summary>
    [Fact]
    public async Task A_completion_the_store_rejects_appends_nothing()
    {
        var run = RunAt("implement");
        var inner = Substitute.For<IWorkflowStore>();
        inner.FindAsync(run.Id, Arg.Any<CancellationToken>()).Returns(new ValueTask<WorkflowRun?>(run));
        inner.CompleteNodeAsync(Guid.Empty, default, default!, default!, default).ReturnsForAnyArgs(_ => throw new InvalidOperationException("stale seq"));

        var complete = async () => await Store(inner).CompleteNodeAsync(
            run.Id, 3, AnyTransition(), new NodeResult("changed", new Dictionary<string, object?>(StringComparer.Ordinal)) { Usage = Usage }, CancellationToken.None);

        await complete.Should().ThrowAsync<InvalidOperationException>();
        await _records.DidNotReceiveWithAnyArgs().AppendAsync(default!, default);
    }

    /// <summary>
    ///     The completion is already persisted when the append runs, so a failed append is logged and the completion
    ///     stands; the next boot's backfill writes the record. Red: rethrow in the recorder; the call throws.
    /// </summary>
    [Fact]
    public async Task A_failed_append_is_logged_and_the_completion_still_stands()
    {
        var run = RunAt("implement");
        var inner = new RecordingWorkflowStore(run);
        _records.AppendAsync(default!, default).ReturnsForAnyArgs(_ => throw new InvalidOperationException("database down"));

        var complete = async () => await Store(inner).CompleteNodeAsync(
            run.Id, 3, AnyTransition(), new NodeResult("changed", new Dictionary<string, object?>(StringComparer.Ordinal)) { Usage = Usage }, CancellationToken.None);

        await complete.Should().NotThrowAsync();
        inner.Completed.Should().NotBeNull();
    }

    /// <summary>
    ///     A review node's variables are dropped before the inner store, but its usage is not a variable and is still
    ///     recorded; the node costs tokens like any other.
    ///     Red: drop <c>Usage</c> from the result <c>DropReviewVariables</c> builds; the append is never received.
    /// </summary>
    [Fact]
    public async Task A_review_node_drops_its_variables_but_still_records_its_usage()
    {
        var run = RunAt("review");
        var inner = new RecordingWorkflowStore(run);
        var report = new Dictionary<string, object?>(StringComparer.Ordinal) { ["notes"] = "v" };

        await Store(inner).CompleteNodeAsync(run.Id, 3, AnyTransition(), new NodeResult("approved", report) { Usage = Usage }, CancellationToken.None);

        inner.Completed!.Variables.Should().BeEmpty("a review node's reported variables are dropped");
        await _records.Received(1).AppendAsync(
            Arg.Is<WorkflowRunRecord>(r => r.Node == "review" && r.Kind == WorkflowRunRecord.NodeUsageKind),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     The completion is durable before the append, so a dispatch cancelled in between must still get its record and
    ///     must not see an exception. Red: pass the caller's token to the append; the store sees a cancelled token and
    ///     the record is never written.
    /// </summary>
    [Fact]
    public async Task A_dispatch_cancelled_after_the_completion_still_records_its_usage()
    {
        var run = RunAt("implement");
        var written = new List<WorkflowRunRecord>();
        _records.AppendAsync(default!, default).ReturnsForAnyArgs(call =>
        {
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            written.Add(call.Arg<WorkflowRunRecord>());
            return ValueTask.CompletedTask;
        });
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var complete = async () => await Store(new RecordingWorkflowStore(run)).CompleteNodeAsync(
            run.Id, 3, AnyTransition(), new NodeResult("changed", new Dictionary<string, object?>(StringComparer.Ordinal)) { Usage = Usage }, cancelled.Token);

        await complete.Should().NotThrowAsync();
        written.Should().ContainSingle().Which.Kind.Should().Be(WorkflowRunRecord.NodeUsageKind);
    }
}
