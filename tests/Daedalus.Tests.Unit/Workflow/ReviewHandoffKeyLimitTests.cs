using System.Globalization;
using System.Reflection;
using Daedalus.Agents.Workflow;
using Thalos.Workflow;
using ZeroAlloc.Results;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Covers the half of <see cref="ReviewHandoffWorkflowStore"/> that puts Thalos' sixteen-key cap back in
///     force on a review node. Narrowing the bag a review dispatch is built from also narrows what
///     <c>WorkflowNodeDispatcher.BuildNodeResult</c> counts that node's report against, so on exactly the node
///     the projection applies to, the cap stopped binding: <c>review</c> may report eight fresh keys on each of
///     five permitted visits and the dispatcher computes two plus eight every time.
/// </summary>
/// <remarks>
///     The consequence is not cosmetic. <c>WorkflowVariableBlock.MaxOmittedKeyListLength</c> is derived from
///     "a bag holds at most <c>MaxVariableKeys</c> keys" and documents its own cut as a backstop that cannot
///     fire while that derivation holds. A bag past forty keys makes it fire, and a fired cut means the next
///     <c>implement</c> turn is handed a task text that omits keys the omission notice does not name.
/// </remarks>
public sealed class ReviewHandoffKeyLimitTests
{
    private const string ProcessName = "manufacture";

    private static WorkflowRun RunWith(string node, params string[] keys) => new()
    {
        Id = Guid.NewGuid(),
        Process = ProcessName,
        ProcessVersion = 4,
        CurrentNode = node,
        CurrentSeq = 3,
        Status = WorkflowStatus.Running,
        Visits = new Dictionary<string, int>(StringComparer.Ordinal) { [node] = 1 },
        Variables = keys.ToDictionary(k => k, k => (object?)"value", StringComparer.Ordinal),
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
                ["implement"] = new() { Agent = "implementer", Skill = "manufacture-implement", Next = "review" },
                ["review"] = new()
                {
                    Agent = "reviewer",
                    Skill = "manufacture-review",
                    Outcomes = ["approved", "rejected"],
                    Branch = new Dictionary<string, string>(StringComparer.Ordinal) { ["approved"] = "done", ["rejected"] = "implement" },
                    Lenses = ["correctness", "falsifiability", "mechanism"],
                },
                ["done"] = new() { Terminal = "succeeded" },
            },
        };

        var store = Substitute.For<IProcessDefinitionStore>();
        store.GetAsync(ProcessName, 4, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result<ProcessDefinition>>(Result<ProcessDefinition>.Success(definition)));
        return store;
    }

    private static Dictionary<string, object?> Report(string prefix, int count) =>
        Enumerable.Range(0, count).ToDictionary(
            i => prefix + i.ToString(CultureInfo.InvariantCulture),
            _ => (object?)"value",
            StringComparer.Ordinal);

    private static string[] Keys(string prefix, int count) =>
        [.. Enumerable.Range(0, count).Select(i => prefix + i.ToString(CultureInfo.InvariantCulture))];

    private static WorkflowTransition AnyTransition() =>
        new("implement", WorkflowStatus.Running, awaitingSignal: null, WorkflowEventKind.Branched);

    /// <summary>
    ///     The shape the finding describes: a review node whose real bag is already near the cap reports fresh
    ///     keys that take it past. The dispatcher could not have caught this - it counted the projected bag.
    /// </summary>
    [Fact]
    public async Task A_review_node_report_that_takes_the_real_bag_past_the_cap_fails_the_run()
    {
        var inner = new RecordingStore(RunWith("review", Keys("standing", 15)));
        var store = new ReviewHandoffWorkflowStore(inner, Definitions());

        await store.CompleteNodeAsync(inner.Run!.Id, 3, AnyTransition(), new NodeResult("rejected", Report("fresh", 2)), CancellationToken.None);

        // Falsifiable: deleting the CompleteNodeAsync override, or counting this.FindAsync's projected bag
        // instead of Inner's, turns this red. Verified by doing both.
        inner.Completed.Should().BeNull("a report over the cap must not be persisted as a transition");
        inner.FailureMessage.Should().NotBeNull();
        inner.FailureMessage.Should().Contain("17", "the message must name the count the bag would reach");
        inner.FailureMessage.Should().Contain("16", "and the limit it passes");
        inner.FailureMessage.Should().Contain("review", "and the node whose report was rejected");
    }

    /// <summary>
    ///     The mirror assertion, without which the one above would be satisfied by a decorator that failed
    ///     every review transition.
    /// </summary>
    [Fact]
    public async Task A_review_node_report_that_stays_inside_the_cap_is_completed_normally()
    {
        var inner = new RecordingStore(RunWith("review", Keys("standing", 14)));
        var store = new ReviewHandoffWorkflowStore(inner, Definitions());

        await store.CompleteNodeAsync(inner.Run!.Id, 3, AnyTransition(), new NodeResult("rejected", Report("fresh", 2)), CancellationToken.None);

        inner.FailureMessage.Should().BeNull();
        inner.Completed.Should().NotBeNull();
        inner.Completed!.Outcome.Should().Be("rejected");
        inner.Completed.Variables.Should().HaveCount(2, "the decorator checks the report, it never rewrites it");
    }

    /// <summary>
    ///     Overwriting a key the run already holds never moves the distinct count - the rule that lets a capped
    ///     loop overwrite the same few keys lap after lap. Stated as its own assertion because a check written
    ///     as "bag count plus report count" would pass both tests above and fail every real five-lap review.
    /// </summary>
    [Fact]
    public async Task A_review_node_that_only_overwrites_existing_keys_is_never_capped()
    {
        var inner = new RecordingStore(RunWith("review", Keys("standing", 16)));
        var store = new ReviewHandoffWorkflowStore(inner, Definitions());

        var overwrite = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["standing0"] = "new value",
            ["standing1"] = "new value",
        };
        await store.CompleteNodeAsync(inner.Run!.Id, 3, AnyTransition(), new NodeResult("rejected", overwrite), CancellationToken.None);

        inner.FailureMessage.Should().BeNull();
        inner.Completed.Should().NotBeNull();
    }

    /// <summary>
    ///     A node that declares no lenses was counted by the dispatcher against the same bag this would read,
    ///     so re-checking it would put the same rule in two places and cost a round trip per transition.
    /// </summary>
    [Fact]
    public async Task A_node_that_runs_no_lenses_is_not_re_checked_here()
    {
        var inner = new RecordingStore(RunWith("implement", Keys("standing", 20)));
        var store = new ReviewHandoffWorkflowStore(inner, Definitions());

        await store.CompleteNodeAsync(inner.Run!.Id, 3, AnyTransition(), new NodeResult("changed", Report("fresh", 4)), CancellationToken.None);

        inner.FailureMessage.Should().BeNull("Thalos already counted this node's report against the real bag");
        inner.Completed.Should().NotBeNull();
    }

    /// <summary>
    ///     <see cref="ReviewHandoffWorkflowStore.MaxVariableKeys"/> restates a value that lives in a type
    ///     <c>Thalos.NET.Workflow</c> keeps internal. A restated constant is a claim about another package, and
    ///     enforcing a different number here would be worse than enforcing none: the shared bound is what makes
    ///     the omitted-key list provably complete.
    /// </summary>
    [Fact]
    public void The_restated_key_cap_still_matches_the_one_thalos_enforces()
    {
        var thalosType = typeof(WorkflowRun).Assembly.GetType("Thalos.Workflow.WorkflowVariableBlock", throwOnError: true)!;
        var field = thalosType.GetField("MaxVariableKeys", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        // If the field moves or is renamed, this must fail loudly rather than skip: a drift guard that quietly
        // stops looking is the shape this codebase keeps producing.
        field.Should().NotBeNull("Thalos.Workflow.WorkflowVariableBlock.MaxVariableKeys is what this constant mirrors");
        field!.GetRawConstantValue().Should().Be(ReviewHandoffWorkflowStore.MaxVariableKeys);
    }

    /// <summary>An <see cref="IWorkflowStore"/> that answers one run and records which member was called.</summary>
    private sealed class RecordingStore(WorkflowRun run) : IWorkflowStore
    {
        public WorkflowRun? Run { get; } = run;

        public NodeResult? Completed { get; private set; }

        public string? FailureMessage { get; private set; }

        public ValueTask<WorkflowRun?> FindAsync(Guid runId, CancellationToken ct) => new(Run);

        public ValueTask CompleteNodeAsync(Guid runId, long seq, WorkflowTransition transition, NodeResult result, CancellationToken ct)
        {
            Completed = result;
            return ValueTask.CompletedTask;
        }

        public ValueTask FailAsync(Guid runId, string errorMessage, CancellationToken ct)
        {
            FailureMessage = errorMessage;
            return ValueTask.CompletedTask;
        }

        public ValueTask<Guid> StartAsync(string process, int version, string correlationKey, string startNode, IReadOnlyDictionary<string, object?>? initialVariables, CancellationToken ct) =>
            throw new NotSupportedException();

        public ValueTask<Result> ResumeAsync(Guid runId, string signal, string? payload, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask<bool> FailStrandedAsync(Guid runId, long expectedSeq, string errorMessage, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask CancelAsync(Guid runId, string reason, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<WorkflowRun>> FindStrandedAsync(TimeSpan olderThan, CancellationToken ct) => throw new NotSupportedException();
    }
}
