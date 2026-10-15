using System.Text.Json;
using Daedalus.Agents.Workflow;
using Thalos;
using Thalos.Testing;
using Thalos.Workflow;
using NodeUsage = Daedalus.Domain.Entities.NodeUsage;
using Task = System.Threading.Tasks.Task;
using WorkflowRunRecord = Daedalus.Domain.Entities.WorkflowRunRecord;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Phase 2.10 final review, item 1: a review whose pinned lens ran on another model is recorded per model, so cost
///     analytics prices each model's tokens at its own rate. <see cref="ReviewLensRunner"/> holds its passes' usage in
///     <see cref="NodeUsageRecorder"/>, and the completion's record takes it back as <see cref="NodeUsage.ByModel"/>.
/// </summary>
public sealed class NodeUsageSplitTests
{
    private static readonly TurnUsage Decided1 = new(1_000, 100, "decided") { CacheReadTokens = 400 };
    private static readonly TurnUsage Pinned = new(2_000, 200, "pinned") { CacheWriteTokens = 50 };
    private static readonly TurnUsage Decided2 = new(500, 50, "decided");

    /// <summary>The sum Thalos hands the store: the first pass's model, every pass's tokens.</summary>
    private static readonly TurnUsage Sum = Decided1 + Pinned + Decided2;

    /// <summary>A record store that keeps what is appended.</summary>
    private static (NodeUsageRecorder Recorder, IWorkflowRunRecordStore Records, List<WorkflowRunRecord> Appended) NewRecorder()
    {
        var appended = new List<WorkflowRunRecord>();
        var records = Substitute.For<IWorkflowRunRecordStore>();
        records.AppendAsync(Arg.Do<WorkflowRunRecord>(appended.Add), Arg.Any<CancellationToken>()).Returns(ValueTask.CompletedTask);
        return (TestNodeUsage.Recorder(records), records, appended);
    }

    /// <summary>
    ///     Passes on two models are recorded as two parts, in the order the models first ran, each summed over its passes.
    ///     Red: skip the take in <c>RecordCompletionAsync</c>; the record has no split.
    ///     Red: group the passes by node instead of by model; there is one part and nothing is held.
    ///     Red: key the held split by seq + 1; the completion at seq 4 finds nothing.
    /// </summary>
    [Fact]
    public async Task A_completion_whose_passes_ran_on_two_models_is_recorded_per_model()
    {
        var (recorder, _, appended) = NewRecorder();
        var runId = Guid.NewGuid();

        recorder.HoldPasses(runId, 4, [Decided1, Pinned, Decided2]);
        await recorder.RecordCompletionAsync(runId, 4, "review", null, Sum, CancellationToken.None);

        var usage = NodeUsage.FromPayloadJson(appended.Should().ContainSingle().Subject.PayloadJson).Value;
        usage.Parts.Should().Equal(new NodeUsage("decided", 1_500, 150, 400, 0), new NodeUsage("pinned", 2_000, 200, 0, 50));
        usage.Model.Should().Be("decided");
        usage.InputTokens.Should().Be(3_500);
        recorder.HeldCount.Should().Be(0, "a completion takes what was held for it");
    }

    /// <summary>
    ///     The token counter counts each model's own tokens. Red: count the completion's single usage instead of each part;
    ///     the pinned model's input is never measured and the decided model's is 3 500.
    /// </summary>
    [Fact]
    public async Task The_token_counter_counts_each_models_own_tokens()
    {
        var (recorder, _, _) = NewRecorder();
        var node = $"probe-node-{Guid.NewGuid():N}";
        var runId = Guid.NewGuid();
        using var capture = new TelemetryCapture(ManufactureTelemetry.SourceName);

        recorder.HoldPasses(runId, 4, [Decided1, Pinned, Decided2]);
        await recorder.RecordCompletionAsync(runId, 4, node, null, Sum, CancellationToken.None);

        capture.MeasurementsOf(ManufactureTelemetry.TokensMetricName)
            .Where(m => node.Equals(m.Tags.GetValueOrDefault(ManufactureTelemetry.NodeTag)) && "input".Equals(m.Tags[ManufactureTelemetry.TokenKindTag]))
            .Select(m => (Model: m.Tags[ManufactureTelemetry.ModelTag], m.Value))
            .Should().BeEquivalentTo(new (object?, double)[] { ("decided", 1_500), ("pinned", 2_000) });
    }

    /// <summary>
    ///     Passes that all ran on one model hold nothing, and clear what an earlier delivery of the same node held, so the node
    ///     is recorded as one model as it always was. Red: hold every pass list; the held count is 1 and the record carries a
    ///     one-part split that the domain refuses, so it is recorded unsplit after a warning.
    /// </summary>
    [Fact]
    public async Task Passes_on_one_model_hold_nothing_and_clear_an_earlier_hold()
    {
        var (recorder, _, appended) = NewRecorder();
        var runId = Guid.NewGuid();

        recorder.HoldPasses(runId, 4, [Decided1, Pinned]);
        recorder.HoldPasses(runId, 4, [Decided1, Decided2]);

        recorder.HeldCount.Should().Be(0);
        await recorder.RecordCompletionAsync(runId, 4, "review", null, Decided1 + Decided2, CancellationToken.None);
        using var payload = JsonDocument.Parse(appended.Should().ContainSingle().Subject.PayloadJson);
        payload.RootElement.TryGetProperty("byModel", out _).Should().BeFalse();
    }

    /// <summary>
    ///     A held split that does not sum to the completion's usage belongs to another delivery: the completion is recorded as
    ///     it reports itself, never with parts that disagree with its total. Red: drop the sum check in
    ///     <see cref="NodeUsage.WithSplit"/>; the record carries the stale parts and a total that does not match them.
    /// </summary>
    [Fact]
    public async Task A_held_split_that_does_not_sum_to_the_completion_is_discarded()
    {
        var (recorder, _, appended) = NewRecorder();
        var runId = Guid.NewGuid();

        recorder.HoldPasses(runId, 4, [Decided1, Pinned]);
        await recorder.RecordCompletionAsync(runId, 4, "review", null, Decided1, CancellationToken.None);

        var usage = NodeUsage.FromPayloadJson(appended.Should().ContainSingle().Subject.PayloadJson).Value;
        usage.ByModel.Should().BeNull();
        usage.InputTokens.Should().Be(1_000);
    }

    /// <summary>
    ///     A failed node never completes, so the dispatcher's store forgets what its review held when it fails the run, and
    ///     only that run's. Red: drop the <c>Forget</c> call from <c>ReviewHandoffWorkflowStore.FailAsync</c>; the count stays 3.
    ///     Red: forget every run; the other run's hold is gone too and the count is 0.
    /// </summary>
    [Fact]
    public async Task Failing_a_run_forgets_what_its_review_held()
    {
        var (recorder, _, _) = NewRecorder();
        var failed = Guid.NewGuid();
        var other = Guid.NewGuid();
        recorder.HoldPasses(failed, 4, [Decided1, Pinned]);
        recorder.HoldPasses(failed, 6, [Decided1, Pinned]);
        recorder.HoldPasses(other, 4, [Decided1, Pinned]);
        var store = new ReviewHandoffWorkflowStore(Substitute.For<IWorkflowStore>(), Substitute.For<IProcessDefinitionStore>(), recorder, TestNodeUsage.Responses());

        await store.FailAsync(failed, "the review failed", CancellationToken.None);

        recorder.HeldCount.Should().Be(1);
    }

    /// <summary>
    ///     Round 2: a completion the store refuses, for a stale seq, a lost lease or a cancellation, takes nothing, so the
    ///     dispatcher's store releases what the review held and lets the failure through. Red: drop the catch around the
    ///     inner <c>CompleteNodeAsync</c>; the count stays 1. Red: swallow the failure; nothing is thrown.
    /// </summary>
    [Fact]
    public async Task A_completion_the_store_refuses_releases_what_the_review_held()
    {
        var (recorder, _, _) = NewRecorder();
        var runId = Guid.NewGuid();
        var inner = Substitute.For<IWorkflowStore>();
        inner.FindAsync(runId, Arg.Any<CancellationToken>()).Returns(new ValueTask<WorkflowRun?>(new WorkflowRun
        {
            Id = runId,
            Process = "manufacture",
            ProcessVersion = 1,
            CurrentNode = "review",
            CurrentSeq = 4,
            Status = WorkflowStatus.Running,
            Visits = new Dictionary<string, int>(StringComparer.Ordinal) { ["review"] = 1 },
        }));
        inner.CompleteNodeAsync(runId, 4, Arg.Any<WorkflowTransition>(), Arg.Any<NodeResult>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromException(new WorkflowConcurrencyException("stale seq")));
        var store = new ReviewHandoffWorkflowStore(inner, Substitute.For<IProcessDefinitionStore>(), recorder, TestNodeUsage.Responses());
        recorder.HoldPasses(runId, 4, [Decided1, Pinned]);

        var act = async () => await store.CompleteNodeAsync(
            runId, 4, new WorkflowTransition("done", WorkflowStatus.Running, awaitingSignal: null, WorkflowEventKind.Branched),
            new NodeResult("approved", new Dictionary<string, object?>(StringComparer.Ordinal)) { Usage = Sum },
            CancellationToken.None);

        await act.Should().ThrowAsync<WorkflowConcurrencyException>();
        recorder.HeldCount.Should().Be(0);
    }

    /// <summary>
    ///     Round 2: as an observer of the store, the recorder releases a run's hold when the store commits its cancellation or
    ///     failure by any path, and keeps it on any other change. Reds, each applied once: ignore every change, and the
    ///     cancelled and failed rows keep the hold; forget on every change, and the node-completed row loses it.
    /// </summary>
    [Theory]
    [InlineData(WorkflowRunChangeKind.Cancelled, 0)]
    [InlineData(WorkflowRunChangeKind.Failed, 0)]
    [InlineData(WorkflowRunChangeKind.NodeCompleted, 1)]
    public async Task A_cancelled_or_failed_run_releases_what_its_review_held(WorkflowRunChangeKind kind, int held)
    {
        var (recorder, _, _) = NewRecorder();
        var runId = Guid.NewGuid();
        recorder.HoldPasses(runId, 4, [Decided1, Pinned]);

        await recorder.OnChangedAsync(new WorkflowRunChange(runId, kind, WorkflowStatus.Cancelled, "review", null), CancellationToken.None);

        recorder.HeldCount.Should().Be(held);
    }
}
