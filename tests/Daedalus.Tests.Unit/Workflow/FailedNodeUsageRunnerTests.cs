using System.Collections.Concurrent;
using Daedalus.Agents.Scheduling;
using Daedalus.Agents.Workflow;
using Daedalus.Domain.Entities;
using Thalos;
using Thalos.Workflow;
using ZeroAlloc.Results;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Phase 2.15: <see cref="FailedNodeUsageRunner"/> records the spend of a workflow node whose turn failed, which no
///     completion records. The turns' usage reaches it through <see cref="TurnUsageNotificationPublisher"/>, as Thalos'
///     runtime publishes each turn's <see cref="TurnCompletedNotification"/> or <see cref="TurnFailedNotification"/> inside
///     the turn's own async flow. Each inner runner here publishes through the real publisher before it returns, as the
///     runtime does. The decorator only holds the usage; <see cref="NodeUsageRecorder"/> appends it once the store has
///     committed the run's failure. <see cref="MirroredStore"/> plays the dispatcher's <c>FailAsync</c> the way Thalos 0.17.0's
///     <c>OrmWorkflowStore</c> does: it throws under a cancelled token before anything commits, and otherwise commits and then
///     notifies its observers.
/// </summary>
public sealed class FailedNodeUsageRunnerTests
{
    private static readonly AgentError BudgetStop = AgentError.SubagentBudgetExceeded(150_000, 7, 155_138);

    /// <summary>
    ///     One implement turn stopped by its ceiling after 155 138 tokens: one node-usage record with that usage, at the run's
    ///     seq and node, carrying the run's starter, and the failure is handed back unchanged.
    ///     Red: return before recording on failure; no record is appended.
    ///     Red: return <c>AgentError.Validation</c> with the error's message instead of the error; the error assertion fails.
    ///     Red: record at <c>CurrentSeq + 1</c>, under <c>run.Process</c> instead of the node, or with a null starter; the seq,
    ///     node or starter assertion fails.
    ///     Red: sum no cache reads in the recorder's per-model grouping; the payload assertion fails.
    /// </summary>
    [Fact]
    public async Task A_budget_stopped_node_records_its_spend()
    {
        var records = new CapturingRecordStore();
        var run = RunAt("implement", seq: 3, startedBy: "a-developer");
        var turn = new TurnUsage(150_000, 5_138, "claude-sonnet-5") { CacheReadTokens = 90_000 };

        var result = await Dispatch.For(records, new PublishingRunner(Fail(turn))).RunAsync(run);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(BudgetStop, "the decorator hands the failure back as it came");
        var record = records.Appended.Should().ContainSingle().Subject;
        record.RunId.Should().Be(run.Id);
        record.Seq.Should().Be(3);
        record.Node.Should().Be("implement");
        record.Kind.Should().Be(WorkflowRunRecord.NodeUsageKind);
        record.StartedById.Should().Be("a-developer");
        NodeUsage.FromPayloadJson(record.PayloadJson).Value.Should().Be(new NodeUsage("claude-sonnet-5", 150_000, 5_138, 90_000, 0));
    }

    /// <summary>
    ///     A review whose two first lens passes completed, one on each of two models, and whose third failed: one record
    ///     whose totals and per-model split sum all three turns.
    ///     Red: record only the failed turn's usage; the totals read 300 input and the split is missing.
    ///     Red: reverse the recorder's per-model grouping; the model reads "pinned" and the split's order flips.
    /// </summary>
    [Fact]
    public async Task A_failed_review_records_the_passes_before_it()
    {
        var records = new CapturingRecordStore();
        var run = RunAt("review", seq: 5);
        var inner = new PublishingRunner(
            Completed(new TurnUsage(1_000, 100, "decided")),
            Completed(new TurnUsage(2_000, 200, "pinned")),
            Fail(new TurnUsage(300, 30, "decided")));

        await Dispatch.For(records, inner).RunAsync(run);

        var usage = NodeUsage.FromPayloadJson(records.Appended.Should().ContainSingle().Subject.PayloadJson).Value;
        usage.InputTokens.Should().Be(3_300);
        usage.OutputTokens.Should().Be(330);
        usage.Model.Should().Be("decided", "a split node's model is the first one it ran on");
        usage.ByModel.Should().Equal(new NodeUsage("decided", 1_300, 130, 0, 0), new NodeUsage("pinned", 2_000, 200, 0, 0));
    }

    /// <summary>
    ///     A turn that failed before any notification was published, such as a validation refusal before the runtime
    ///     started it, records nothing.
    ///     Red: record an empty usage when no turn was captured, by dropping both the runner's no-turn guard and the
    ///     recorder's zero-usage guard; a zero record is appended. Either guard alone keeps this green.
    /// </summary>
    [Fact]
    public async Task A_failure_with_no_turn_records_nothing()
    {
        var records = new CapturingRecordStore();

        var result = await Dispatch.For(records, new PublishingRunner(Fail(null))).RunAsync(RunAt("implement"));

        result.IsFailure.Should().BeTrue();
        records.Appended.Should().BeEmpty();
    }

    /// <summary>
    ///     A completed node is recorded by the completion path, <see cref="NodeUsageRecorder.RecordCompletionAsync"/>, so this
    ///     decorator records nothing for it.
    ///     Red: record on success too; the completion path would then count the node twice.
    /// </summary>
    [Fact]
    public async Task A_successful_node_records_nothing_here()
    {
        var records = new CapturingRecordStore();

        var result = await Dispatch.For(records, new PublishingRunner(Completed(new TurnUsage(1_000, 100, "m")))).RunAsync(RunAt("implement"));

        result.IsSuccess.Should().BeTrue();
        records.Appended.Should().BeEmpty();
    }

    /// <summary>
    ///     Two dispatches of two runs, started together, both inside their turns before either publishes and both published
    ///     before either returns, each record only their own turn.
    ///     Red: make <c>NodeTurnUsageScope.Current</c> a static field instead of an <c>AsyncLocal</c>; the second scope
    ///     replaces the first, both turns land in it, and one run records both while the other records nothing.
    /// </summary>
    [Fact]
    public async Task Concurrent_dispatches_keep_their_own_turns()
    {
        var records = new CapturingRecordStore();
        var first = RunAt("implement");
        var second = RunAt("implement");
        var dispatch = Dispatch.For(records, new GatedRunner(new CountdownGate(2), new CountdownGate(2), new Dictionary<Guid, TurnUsage>
        {
            [first.Id] = new(1_000, 10, "m"),
            [second.Id] = new(2_000, 20, "m"),
        }));

        await Task.WhenAll(DispatchAsync(first), DispatchAsync(second));

        async Task DispatchAsync(WorkflowRun run) => await dispatch.RunAsync(run);

        var byRun = records.Appended.ToDictionary(r => r.RunId, r => NodeUsage.FromPayloadJson(r.PayloadJson).Value.InputTokens);
        byRun.Should().BeEquivalentTo(new Dictionary<Guid, long> { [first.Id] = 1_000, [second.Id] = 2_000 });
    }

    /// <summary>
    ///     A scheduled run's turn is no workflow node: the decorator passes it through, opens no scope and records nothing,
    ///     even when it fails after spending.
    ///     Red: drop the caller check; the turn would be recorded, under a run that does not exist.
    /// </summary>
    [Fact]
    public async Task A_non_workflow_request_opens_no_scope()
    {
        var records = new CapturingRecordStore();
        NodeTurnUsageScope? seen = null;
        var inner = new PublishingRunner(Fail(new TurnUsage(1_000, 10, "m"))) { OnRun = () => seen = NodeTurnUsageScope.Current };
        var request = new SubagentRunRequest
        {
            AgentId = AgentId.New(),
            Task = "scout",
            Caller = new DetachedPrincipal("schedule:daedalus", ["reader"]),
        };

        var result = await new FailedNodeUsageRunner(inner, TestNodeUsage.Recorder(records)).RunAsync(request, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        seen.Should().BeNull("only a workflow node's turns are collected");
        records.Appended.Should().BeEmpty();
    }

    /// <summary>
    ///     The final review's case: a graceful stop cancels the dispatch, the turn fails as cancelled after spending, and the
    ///     dispatcher's <c>FailAsync</c> throws under the cancelled token, so the failure never commits. The message is
    ///     redelivered, the node runs again at the same seq and completes, and a later node then fails the run. Only the
    ///     rerun's completion is recorded at that seq.
    ///     Red: append the failure from the decorator, before the dispatcher fails the run, as before this fix; the cancelled
    ///     attempt is appended too, and the no-record assertion fails.
    ///     Red: drop the decorator's <c>DropFailure</c> call; the cancelled attempt's hold survives the redelivery, the later
    ///     failure appends it, and the single-record assertion fails.
    /// </summary>
    [Fact]
    public async Task A_cancelled_dispatch_whose_failure_does_not_commit_records_nothing()
    {
        var records = new CapturingRecordStore();
        var recorder = TestNodeUsage.Recorder(records);
        var store = new MirroredStore(recorder);
        var run = RunAt("implement", seq: 3);
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();

        await new Dispatch(recorder, store, new PublishingRunner(Fail(new TurnUsage(40_000, 400, "m"))))
            .Invoking(d => d.RunAsync(run, stopping.Token)).Should().ThrowAsync<OperationCanceledException>();
        records.Appended.Should().BeEmpty("the run's failure never committed");

        var rerun = new TurnUsage(150_000, 5_138, "m");
        (await new Dispatch(recorder, store, new PublishingRunner(Completed(rerun))).RunAsync(run)).IsSuccess.Should().BeTrue();
        await recorder.RecordCompletionAsync(run.Id, 3, "implement", null, rerun, CancellationToken.None);
        await store.FailAsync(RunAt("review", seq: 4) with { Id = run.Id }, CancellationToken.None);

        records.Appended.Should().ContainSingle("only the rerun's completion is recorded at seq 3");
    }

    /// <summary>
    ///     A failure that commits is recorded once, at the seq it was held for, and the hold is released.
    ///     Red: append nothing from <c>OnChangedAsync</c> on <c>Failed</c>; no record is written.
    ///     Red: append at <c>key.Seq + 1</c>; the seq assertion fails.
    ///     Red: read the hold with <c>TryGetValue</c> instead of taking it with <c>TryRemove</c>; the hold count stays 1.
    /// </summary>
    [Fact]
    public async Task A_failure_that_commits_is_recorded_once_at_its_seq()
    {
        var records = new CapturingRecordStore();
        var recorder = TestNodeUsage.Recorder(records);
        var run = RunAt("implement", seq: 7);

        await new Dispatch(recorder, new MirroredStore(recorder), new PublishingRunner(Fail(new TurnUsage(12_000, 340, "m")))).RunAsync(run);

        records.Appended.Should().ContainSingle().Which.Seq.Should().Be(7);
        recorder.HeldFailureCount.Should().Be(0, "the committed failure took what was held");
    }

    /// <summary>
    ///     A run whose turn failed but which the store then cancels, rather than fails, records nothing: the cancellation
    ///     releases the hold.
    ///     Red: append on <c>Cancelled</c> as on <c>Failed</c>; a record is written.
    ///     Red: drop nothing on <c>Cancelled</c>; the hold count stays 1.
    /// </summary>
    [Fact]
    public async Task A_committed_cancellation_releases_a_held_failure()
    {
        var records = new CapturingRecordStore();
        var recorder = TestNodeUsage.Recorder(records);
        var run = RunAt("implement", seq: 2);
        (await new FailedNodeUsageRunner(new PublishingRunner(Fail(new TurnUsage(9_000, 90, "m"))), recorder)
            .RunAsync(RequestFor(run), CancellationToken.None)).IsFailure.Should().BeTrue();
        recorder.HeldFailureCount.Should().Be(1, "otherwise the release below is not what empties it");

        await recorder.OnChangedAsync(
            new WorkflowRunChange(run.Id, WorkflowRunChangeKind.Cancelled, WorkflowStatus.Cancelled, "implement", null), CancellationToken.None);

        records.Appended.Should().BeEmpty();
        recorder.HeldFailureCount.Should().Be(0);
    }

    /// <summary>
    ///     Fix round 2: the stranded sweep failed the run while its node's turn ran, so that commit found nothing held. The
    ///     turn then fails, and the dispatcher's <c>FailAsync</c> reaches a run that is already terminal, which commits nothing
    ///     and notifies nobody. <see cref="ReviewHandoffWorkflowStore"/> reads the run back as Failed and appends the held
    ///     record, once.
    ///     Red: drop the settle call from <c>ReviewHandoffWorkflowStore.FailAsync</c>; no record is written, and the hold
    ///     stays.
    /// </summary>
    [Fact]
    public async Task A_failure_held_for_a_run_already_failed_is_appended_when_the_dispatcher_fails_it()
    {
        var records = new CapturingRecordStore();
        var recorder = TestNodeUsage.Recorder(records);
        var run = RunAt("implement", seq: 4) with { Status = WorkflowStatus.Failed };
        var store = new ReviewHandoffWorkflowStore(
            new RecordingWorkflowStore(run), HostWrittenKeyTests.Definitions(), recorder, TestNodeUsage.Responses());

        await FailThroughAsync(store, recorder, run);

        records.Appended.Should().ContainSingle().Which.Seq.Should().Be(4);
        recorder.HeldFailureCount.Should().Be(0);
    }

    /// <summary>
    ///     Fix round 2: a run cancelled while its node's turn ran. The dispatcher's <c>FailAsync</c> reaches a run already
    ///     cancelled, and <see cref="ReviewHandoffWorkflowStore"/> reads it back as Cancelled and releases the hold without
    ///     a record.
    ///     Red: settle a Cancelled run as a Failed one; a record is written.
    ///     Red: drop the settle call from <c>ReviewHandoffWorkflowStore.FailAsync</c>; the hold count stays 1.
    /// </summary>
    [Fact]
    public async Task A_failure_held_for_a_run_already_cancelled_is_released_when_the_dispatcher_fails_it()
    {
        var records = new CapturingRecordStore();
        var recorder = TestNodeUsage.Recorder(records);
        var run = RunAt("implement", seq: 4) with { Status = WorkflowStatus.Cancelled };
        var store = new ReviewHandoffWorkflowStore(
            new RecordingWorkflowStore(run), HostWrittenKeyTests.Definitions(), recorder, TestNodeUsage.Responses());

        await FailThroughAsync(store, recorder, run);

        records.Appended.Should().BeEmpty();
        recorder.HeldFailureCount.Should().Be(0);
    }

    /// <summary>
    ///     Fix round 2, the ordinary path: the store commits the failure and its observer appends the record, then
    ///     <see cref="ReviewHandoffWorkflowStore"/> reads the run back as Failed and settles again, which finds nothing.
    ///     Red: read the hold with <c>TryGetValue</c> instead of taking it with <c>TryRemove</c> in
    ///     <c>SettleFailuresAsync</c>; the record is appended twice.
    /// </summary>
    [Fact]
    public async Task The_observer_and_the_store_settling_the_same_failure_append_it_once()
    {
        var records = new CapturingRecordStore();
        var recorder = TestNodeUsage.Recorder(records);
        var run = RunAt("implement", seq: 4);
        var inner = new CommittingStore(new RecordingWorkflowStore(run with { Status = WorkflowStatus.Failed }), recorder, run);
        var store = new ReviewHandoffWorkflowStore(inner, HostWrittenKeyTests.Definitions(), recorder, TestNodeUsage.Responses());

        await FailThroughAsync(store, recorder, run);

        records.Appended.Should().ContainSingle();
    }

    /// <summary>The node's turn fails after spending, then the dispatcher fails the run through <paramref name="store"/>.</summary>
    private static async Task FailThroughAsync(ReviewHandoffWorkflowStore store, NodeUsageRecorder recorder, WorkflowRun run)
    {
        var result = await new FailedNodeUsageRunner(new PublishingRunner(Fail(new TurnUsage(30_000, 300, "m"))), recorder)
            .RunAsync(RequestFor(run), CancellationToken.None);
        result.IsFailure.Should().BeTrue();
        await store.FailAsync(run.Id, result.Error.ToString(), CancellationToken.None);
    }

    private static WorkflowRun RunAt(string node, long seq = 3, string? startedBy = null) => new()
    {
        Id = Guid.NewGuid(),
        Process = "manufacture",
        ProcessVersion = 3,
        CurrentNode = node,
        CurrentSeq = seq,
        Status = WorkflowStatus.Running,
        Visits = new Dictionary<string, int>(StringComparer.Ordinal) { [node] = 1 },
        Variables = new Dictionary<string, object?>(StringComparer.Ordinal),
        StartedBy = startedBy is null ? null : new RunPrincipal(startedBy, ["developer"]),
    };

    private static SubagentRunRequest RequestFor(WorkflowRun run) => new()
    {
        AgentId = AgentId.New(),
        Task = "Load the skill and follow it.",
        Caller = new WorkflowCaller(run, grant: null),
    };

    private static (TurnUsage? Usage, bool Failed) Completed(TurnUsage usage) => (usage, false);

    private static (TurnUsage? Usage, bool Failed) Fail(TurnUsage? usage) => (usage, true);

    private static ValueTask Publish(TurnUsage usage, bool failed) => failed
        ? Publisher.PublishAsync(
            new TurnFailedNotification(new SessionId(Guid.Empty), TurnId.New(), BudgetStop, DateTimeOffset.UtcNow, usage), CancellationToken.None)
        : Publisher.PublishAsync(
            new TurnCompletedNotification(new SessionId(Guid.Empty), TurnId.New(), usage, TimeSpan.Zero, DateTimeOffset.UtcNow), CancellationToken.None);

    private static readonly TurnUsageNotificationPublisher Publisher = new();

    /// <summary>
    ///     Plays its turns in order, publishing each one's notification through the real publisher from inside the call, as
    ///     the runtime does, and returns the last turn's result: a failure when it failed. A turn with no usage publishes
    ///     nothing.
    /// </summary>
    private sealed class PublishingRunner(params (TurnUsage? Usage, bool Failed)[] turns) : ISubagentRunner
    {
        public Action? OnRun { get; init; }

        private static AgentTurnResult Turn(TurnUsage usage) =>
            new(TurnId.New(), new SessionId(Guid.Empty), "done", usage, [], TimeSpan.FromMilliseconds(1));

        public async ValueTask<Result<AgentTurnResult, AgentError>> RunAsync(SubagentRunRequest request, CancellationToken ct = default)
        {
            OnRun?.Invoke();
            foreach (var (usage, failed) in turns)
            {
                await Task.Yield();
                if (usage is { } spent)
                {
                    await Publish(spent, failed);
                }
            }

            var (last, lastFailed) = turns[^1];
            return lastFailed
                ? Result<AgentTurnResult, AgentError>.Failure(BudgetStop)
                : Result<AgentTurnResult, AgentError>.Success(Turn(last ?? default));
        }
    }

    /// <summary>
    ///     Waits until every dispatch is inside its turn, publishes the failed turn of the request's own run, then waits until
    ///     every dispatch has published before it returns, so no scope closes while another dispatch still publishes.
    /// </summary>
    private sealed class GatedRunner(CountdownGate inside, CountdownGate published, IReadOnlyDictionary<Guid, TurnUsage> usageByRun)
        : ISubagentRunner
    {
        public async ValueTask<Result<AgentTurnResult, AgentError>> RunAsync(SubagentRunRequest request, CancellationToken ct = default)
        {
            var run = ((WorkflowCaller)request.Caller).Run;
            await inside.SignalAndWaitAsync();
            await Publish(usageByRun[run.Id], failed: true);
            await published.SignalAndWaitAsync();
            return Result<AgentTurnResult, AgentError>.Failure(BudgetStop);
        }
    }

    private sealed class CountdownGate(int count)
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _remaining = count;

        public Task SignalAndWaitAsync()
        {
            if (Interlocked.Decrement(ref _remaining) == 0)
            {
                _open.SetResult();
            }

            return _open.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>
    ///     One dispatch the way Thalos' <c>WorkflowNodeDispatcher</c> runs it: the decorated runner, then, on a failure, the
    ///     store's <c>FailAsync</c> under the same token.
    /// </summary>
    private sealed class Dispatch(NodeUsageRecorder recorder, MirroredStore store, ISubagentRunner inner)
    {
        private readonly FailedNodeUsageRunner _runner = new(inner, recorder);

        public async Task<Result<AgentTurnResult, AgentError>> RunAsync(WorkflowRun run, CancellationToken ct = default)
        {
            var result = await _runner.RunAsync(RequestFor(run), ct);
            if (result.IsFailure)
            {
                await store.FailAsync(run, ct);
            }

            return result;
        }

        /// <summary>A dispatch whose recorder appends to <paramref name="records"/> and whose store notifies that recorder.</summary>
        public static Dispatch For(CapturingRecordStore records, ISubagentRunner inner)
        {
            var recorder = TestNodeUsage.Recorder(records);
            return new Dispatch(recorder, new MirroredStore(recorder), inner);
        }
    }

    /// <summary>
    ///     <c>FailAsync</c> as Thalos 0.17.0's <c>OrmWorkflowStore</c> runs it: opening the connection under a cancelled token
    ///     throws before anything commits; otherwise the failure commits and each observer is told after the commit.
    /// </summary>
    private sealed class MirroredStore(IWorkflowRunObserver observer)
    {
        public async ValueTask FailAsync(WorkflowRun run, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            await observer.OnChangedAsync(
                new WorkflowRunChange(run.Id, WorkflowRunChangeKind.Failed, WorkflowStatus.Failed, run.CurrentNode, null), ct);
        }
    }

    /// <summary>
    ///     A store whose <c>FailAsync</c> commits the failure and tells the recorder, as <c>OrmWorkflowStore</c> does on a run
    ///     that was still running; its <c>FindAsync</c> then reads the run as Failed.
    /// </summary>
    private sealed class CommittingStore(IWorkflowStore inner, IWorkflowRunObserver observer, WorkflowRun run)
        : DelegatingWorkflowStore(inner)
    {
        public override async ValueTask FailAsync(Guid runId, string errorMessage, CancellationToken ct)
        {
            await Inner.FailAsync(runId, errorMessage, ct);
            await observer.OnChangedAsync(
                new WorkflowRunChange(run.Id, WorkflowRunChangeKind.Failed, WorkflowStatus.Failed, run.CurrentNode, null), ct);
        }
    }

    /// <summary>Accepts every append and keeps it.</summary>
    private sealed class CapturingRecordStore : IWorkflowRunRecordStore
    {
        private readonly ConcurrentQueue<WorkflowRunRecord> _appended = new();

        public IReadOnlyList<WorkflowRunRecord> Appended => [.. _appended];

        public ValueTask AppendAsync(WorkflowRunRecord record, CancellationToken ct)
        {
            _appended.Enqueue(record);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<WorkflowRunRecord>> ListAsync(Guid runId, string? kind, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<WorkflowRunRecord>>([.. _appended.Where(r => r.RunId == runId && (kind is null || string.Equals(r.Kind, kind, StringComparison.Ordinal)))]);
    }
}
