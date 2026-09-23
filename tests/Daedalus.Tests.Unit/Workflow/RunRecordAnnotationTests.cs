using Daedalus.Agents.Memory;
using Daedalus.Agents.Workflow;
using Thalos;
using Thalos.Memory;
using Thalos.Workflow;
using ZeroAlloc.Authorization;
using ZeroAlloc.Results;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Covers the two things a run's record gains in phase 2.3 task B5: which squad mode produced each
///     transition, and which <see cref="MemoryRecallTier"/> answered the node's last turn. Both travel the same
///     way — <see cref="WorkflowRunModeStore"/> merges them into the <see cref="NodeResult"/> the store writes,
///     which is what <c>OrmWorkflowStore.ApplyTransitionAsync</c> puts, unmerged, into that transition's
///     <c>workflow_run_event</c> row.
/// </summary>
/// <remarks>
///     <c>Daedalus.Tests.Integration.Workflow.SquadHandoffEndToEndTests</c> asserts the same two values out of
///     a real <c>workflow_run_event</c> table. This suite exists alongside it for the cases a live run cannot
///     produce on demand: a degraded recall tier, a recall that never happened, and a node whose turn recalled
///     under a caller that is not a workflow run at all.
/// </remarks>
public sealed class RunRecordAnnotationTests
{
    private static readonly AgentId Reviewer = new(new Guid(0x44444444, 0x5555, 0x4666, 0x87, 0x77, 0x88, 0x88, 0x99, 0x99, 0xaa, 0xaa));

    private static WorkflowRun Run(Guid id) => new()
    {
        Id = id,
        Process = "manufacture",
        ProcessVersion = 3,
        CurrentNode = "review",
        CurrentSeq = 4,
        Status = WorkflowStatus.Running,
        Visits = new Dictionary<string, int>(StringComparer.Ordinal) { ["review"] = 1 },
    };

    private static WorkflowTransition AnyTransition() =>
        new("done", WorkflowStatus.Running, awaitingSignal: null, WorkflowEventKind.Branched);

    /// <summary>Captures what the decorator handed the inner store.</summary>
    private sealed class CapturingStore : IWorkflowStore
    {
        public NodeResult? Written { get; private set; }

        public ValueTask CompleteNodeAsync(Guid runId, long seq, WorkflowTransition transition, NodeResult result, CancellationToken ct)
        {
            Written = result;
            return ValueTask.CompletedTask;
        }

        public ValueTask<Guid> StartAsync(string process, int version, string correlationKey, string startNode, IReadOnlyDictionary<string, object?>? initialVariables, CancellationToken ct) =>
            throw new NotSupportedException();

        public ValueTask<WorkflowRun?> FindAsync(Guid runId, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask<Result> ResumeAsync(Guid runId, string signal, string? payload, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask FailAsync(Guid runId, string errorMessage, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask<bool> FailStrandedAsync(Guid runId, long expectedSeq, string errorMessage, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask CancelAsync(Guid runId, string reason, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<WorkflowRun>> FindStrandedAsync(TimeSpan olderThan, CancellationToken ct) => throw new NotSupportedException();
    }

    private static IMemoryService ServiceAnswering(MemoryRecallTier tier)
    {
        var inner = Substitute.For<IMemoryService>();
        inner.RecallAsync(Arg.Any<string>(), Arg.Any<MemoryScope>(), Arg.Any<RecallOptions>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result<MemoryRecallResult, AgentError>>(
                Result<MemoryRecallResult, AgentError>.Success(new MemoryRecallResult([], tier))));
        return inner;
    }

    private static async Task<NodeResult> RecallThenCompleteAsync(Guid runId, MemoryRecallTier? tier)
    {
        var log = new WorkflowRecallTierLog();
        if (tier is { } answered)
        {
            var caller = new WorkflowCaller(Run(runId));
            var memory = new RecallTierRecordingMemoryService(ServiceAnswering(answered), log, () => caller);
            await memory.RecallAsync("anything", new MemoryScope(caller.MemoryOwnerId, Reviewer, "daedalus"), new RecallOptions(), CancellationToken.None);
        }

        var store = new CapturingStore();
        var decorated = new WorkflowRunModeStore(store, new SquadOptions { Enabled = true, FallbackAgentName = "Daedalus Architect" }, log);
        await decorated.CompleteNodeAsync(runId, 4, AnyTransition(), new NodeResult("approved", new Dictionary<string, object?>(StringComparer.Ordinal)), CancellationToken.None);

        store.Written.Should().NotBeNull();
        return store.Written!;
    }

    [Fact]
    public async Task A_semantic_recall_is_recorded_on_the_nodes_transition()
    {
        var written = await RecallThenCompleteAsync(Guid.NewGuid(), MemoryRecallTier.Semantic);

        written.Variables.Should().ContainKey(WorkflowRunModeStore.RecallTierKey)
            .WhoseValue.Should().Be(nameof(MemoryRecallTier.Semantic));
    }

    /// <summary>
    ///     The tier is only worth recording if the degraded one is tellable apart from the good one — the whole
    ///     point of design section 5.4 is that "tier 2, index cold" is auditable and silence is not. Asserted as
    ///     two separate values rather than "the key is present", which a constant would also satisfy.
    /// </summary>
    [Theory]
    [InlineData(MemoryRecallTier.Recency)]
    [InlineData(MemoryRecallTier.None)]
    public async Task A_degraded_recall_is_recorded_as_the_tier_that_actually_answered(MemoryRecallTier tier)
    {
        var written = await RecallThenCompleteAsync(Guid.NewGuid(), tier);

        written.Variables[WorkflowRunModeStore.RecallTierKey].Should().Be(tier.ToString());
        written.Variables[WorkflowRunModeStore.RecallTierKey].Should().NotBe(nameof(MemoryRecallTier.Semantic));
    }

    /// <summary>
    ///     "This turn recalled nothing at all" and "it recalled and found nothing in scope"
    ///     (<see cref="MemoryRecallTier.None"/>) are different statements, and conflating them would reinstate
    ///     the silence this mechanism exists to remove — one level up.
    /// </summary>
    [Fact]
    public async Task A_turn_that_made_no_recall_records_no_tier_rather_than_tier_none()
    {
        var written = await RecallThenCompleteAsync(Guid.NewGuid(), tier: null);

        written.Variables.Should().NotContainKey(WorkflowRunModeStore.RecallTierKey);
    }

    /// <summary>
    ///     The tier is <em>taken</em> from the log, not peeked at. Without that, a node whose turn made no
    ///     recall silently reports the tier of the previous node's turn as its own — the exact failure the tier
    ///     exists to make visible, reappearing one level up, and invisible because the key is still present.
    /// </summary>
    [Fact]
    public async Task A_second_transition_of_the_same_run_does_not_reuse_the_first_transitions_tier()
    {
        var runId = Guid.NewGuid();
        var log = new WorkflowRecallTierLog();
        var caller = new WorkflowCaller(Run(runId));
        var memory = new RecallTierRecordingMemoryService(ServiceAnswering(MemoryRecallTier.Semantic), log, () => caller);
        await memory.RecallAsync("anything", new MemoryScope(caller.MemoryOwnerId, Reviewer, "daedalus"), new RecallOptions(), CancellationToken.None);

        var store = new CapturingStore();
        var decorated = new WorkflowRunModeStore(store, new SquadOptions { Enabled = true, FallbackAgentName = "x" }, log);
        var empty = new Dictionary<string, object?>(StringComparer.Ordinal);

        await decorated.CompleteNodeAsync(runId, 1, AnyTransition(), new NodeResult("changed", empty), CancellationToken.None);
        store.Written!.Variables.Should().ContainKey(WorkflowRunModeStore.RecallTierKey, "the first transition follows a real recall");

        await decorated.CompleteNodeAsync(runId, 2, AnyTransition(), new NodeResult("approved", empty), CancellationToken.None);
        store.Written!.Variables.Should().NotContainKey(WorkflowRunModeStore.RecallTierKey,
            "no turn recalled between the two transitions, so the second must claim nothing");
    }

    /// <summary>A node must not inherit the tier of a recall some other run made.</summary>
    [Fact]
    public async Task A_tier_recorded_for_one_run_is_not_written_onto_another_runs_transition()
    {
        var log = new WorkflowRecallTierLog();
        var otherRun = Guid.NewGuid();
        var caller = new WorkflowCaller(Run(otherRun));
        var memory = new RecallTierRecordingMemoryService(ServiceAnswering(MemoryRecallTier.Recency), log, () => caller);
        await memory.RecallAsync("anything", new MemoryScope(caller.MemoryOwnerId, Reviewer, "daedalus"), new RecallOptions(), CancellationToken.None);

        var store = new CapturingStore();
        var decorated = new WorkflowRunModeStore(store, new SquadOptions { Enabled = true, FallbackAgentName = "x" }, log);
        await decorated.CompleteNodeAsync(Guid.NewGuid(), 4, AnyTransition(), new NodeResult("approved", new Dictionary<string, object?>(StringComparer.Ordinal)), CancellationToken.None);

        store.Written!.Variables.Should().NotContainKey(WorkflowRunModeStore.RecallTierKey);
    }

    /// <summary>A recall made outside a workflow run has no run to attribute a tier to.</summary>
    [Fact]
    public async Task A_recall_by_a_caller_that_is_not_a_workflow_run_records_nothing()
    {
        var log = new WorkflowRecallTierLog();
        var memory = new RecallTierRecordingMemoryService(
            ServiceAnswering(MemoryRecallTier.Recency), log, () => Substitute.For<ISecurityContext>());

        await memory.RecallAsync("anything", new MemoryScope("someone", Reviewer, "daedalus"), new RecallOptions(), CancellationToken.None);

        var store = new CapturingStore();
        var decorated = new WorkflowRunModeStore(store, new SquadOptions { Enabled = true, FallbackAgentName = "x" }, log);
        await decorated.CompleteNodeAsync(Guid.NewGuid(), 4, AnyTransition(), new NodeResult("approved", new Dictionary<string, object?>(StringComparer.Ordinal)), CancellationToken.None);

        store.Written!.Variables.Should().NotContainKey(WorkflowRunModeStore.RecallTierKey);
    }

    /// <summary>A failed recall has no tier to claim, so nothing is recorded for it.</summary>
    [Fact]
    public async Task A_failed_recall_records_no_tier()
    {
        var log = new WorkflowRecallTierLog();
        var runId = Guid.NewGuid();
        var caller = new WorkflowCaller(Run(runId));
        var inner = Substitute.For<IMemoryService>();
        inner.RecallAsync(Arg.Any<string>(), Arg.Any<MemoryScope>(), Arg.Any<RecallOptions>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result<MemoryRecallResult, AgentError>>(
                Result<MemoryRecallResult, AgentError>.Failure(AgentError.Validation("the store is down"))));

        var memory = new RecallTierRecordingMemoryService(inner, log, () => caller);
        var result = await memory.RecallAsync("anything", new MemoryScope(caller.MemoryOwnerId, Reviewer, "daedalus"), new RecallOptions(), CancellationToken.None);

        result.IsFailure.Should().BeTrue("the decorator must pass the failure through, not swallow it");
        log.Take(runId).Should().BeNull();
    }

    [Fact]
    public async Task Every_transition_carries_the_squad_mode_that_produced_it()
    {
        var store = new CapturingStore();
        var decorated = new WorkflowRunModeStore(
            store, new SquadOptions { Enabled = false, FallbackAgentName = "Daedalus Architect" }, new WorkflowRecallTierLog());

        await decorated.CompleteNodeAsync(Guid.NewGuid(), 1, AnyTransition(), new NodeResult("changed", new Dictionary<string, object?>(StringComparer.Ordinal)), CancellationToken.None);

        var mode = store.Written!.Variables[WorkflowRunModeStore.SquadModeKey]!.ToString();
        mode.Should().StartWith(WorkflowRunModeStore.SquadDisabledPrefix);
        mode.Should().Contain("Daedalus Architect", "the record has to name the agent that did both halves");
        mode.Should().Contain("implemented and reviewed its own work",
            "a bare flag does not tell a human six months later what the mode cost them");
    }

    [Fact]
    public void The_enabled_and_disabled_descriptions_are_not_the_same_string()
    {
        var enabled = WorkflowRunModeStore.DescribeMode(new SquadOptions { Enabled = true, FallbackAgentName = "Daedalus Architect" });
        var disabled = WorkflowRunModeStore.DescribeMode(new SquadOptions { Enabled = false, FallbackAgentName = "Daedalus Architect" });

        enabled.Should().Be(WorkflowRunModeStore.SquadEnabledValue);
        enabled.Should().NotBe(disabled, "a mode recorded identically in both polarities records nothing");
    }

    /// <summary>The decorator adds to a node's report; it never replaces or drops what the node itself said.</summary>
    [Fact]
    public async Task A_nodes_own_reported_variables_survive_the_annotation()
    {
        var store = new CapturingStore();
        var decorated = new WorkflowRunModeStore(
            store, new SquadOptions { Enabled = true, FallbackAgentName = "x" }, new WorkflowRecallTierLog());

        var reported = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [ReviewHandoff.FilesTouchedKey] = "src/A.cs",
            [ReviewHandoff.SummaryKey] = "did a thing",
        };
        await decorated.CompleteNodeAsync(Guid.NewGuid(), 1, AnyTransition(), new NodeResult("changed", reported), CancellationToken.None);

        store.Written!.Outcome.Should().Be("changed");
        store.Written.Variables[ReviewHandoff.FilesTouchedKey].Should().Be("src/A.cs");
        store.Written.Variables[ReviewHandoff.SummaryKey].Should().Be("did a thing");
    }
}
