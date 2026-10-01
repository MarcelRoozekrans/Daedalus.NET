using System.Globalization;
using System.Reflection;
using Daedalus.Agents.Workflow;
using Thalos.Workflow;
using ZeroAlloc.Results;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Covers the half of <see cref="ReviewHandoffWorkflowStore"/> that puts Thalos' sixteen-key cap back in
///     force on a review node, and, since the final review's I3, keeps room in every agent node's count for the keys
///     the host adds on every transition and the ones <c>open-pull-request</c> writes. Narrowing the bag a review dispatch is built from also narrows what
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
    ///     keys that take it past. The dispatcher could not have caught this - it counted the projected bag. Since the
    ///     final review's I3, the count includes the three keys WorkflowRunModeStore adds and the two open-pull-request
    ///     writes: ten keys plus two fresh ones is twelve, and seventeen with those five.
    /// </summary>
    [Fact]
    public async Task A_review_node_report_that_takes_the_real_bag_past_the_cap_fails_the_run()
    {
        var inner = new RecordingWorkflowStore(RunWith("review", Keys("standing", 10)));
        var store = new ReviewHandoffWorkflowStore(inner, Definitions());

        await store.CompleteNodeAsync(inner.Run!.Id, 3, AnyTransition(), new NodeResult("rejected", Report("fresh", 2)), CancellationToken.None);

        // Falsifiable: deleting the CompleteNodeAsync override, or counting this.FindAsync's projected bag
        // instead of Inner's, turns this red. Verified by doing both.
        inner.Completed.Should().BeNull("a report over the cap must not be persisted as a transition");
        inner.FailureMessage.Should().NotBeNull();
        inner.FailureMessage.Should().Contain("to 12 distinct keys", "the message must name the count the report takes the bag to");
        inner.FailureMessage.Should().Contain("to 17 with", "and the count with the host's keys");
        inner.FailureMessage.Should().Contain("limit of 16", "and the limit it passes");
        inner.FailureMessage.Should().Contain("pr_url", "and name the room it keeps");
        inner.FailureMessage.Should().Contain("'review'", "and the node whose report was rejected");
    }

    /// <summary>
    ///     The mirror assertion, without which the one above would be satisfied by a decorator that failed
    ///     every review transition: nine keys plus two fresh ones plus the host's five is exactly sixteen.
    /// </summary>
    [Fact]
    public async Task A_review_node_report_that_stays_inside_the_cap_is_completed_normally()
    {
        var inner = new RecordingWorkflowStore(RunWith("review", Keys("standing", 9)));
        var store = new ReviewHandoffWorkflowStore(inner, Definitions());

        await store.CompleteNodeAsync(inner.Run!.Id, 3, AnyTransition(), new NodeResult("rejected", Report("fresh", 2)), CancellationToken.None);

        inner.FailureMessage.Should().BeNull();
        inner.Completed.Should().NotBeNull();
        inner.Completed!.Outcome.Should().Be("rejected");
        inner.Completed.Variables.Should().HaveCount(2, "the decorator checks the report, and rewrites it only to enforce authorship");
    }

    /// <summary>
    ///     Overwriting a key the run already holds never moves the distinct count - the rule that lets a capped
    ///     loop overwrite the same few keys lap after lap. Stated as its own assertion because a check written
    ///     as "bag count plus report count" would pass both tests above and fail every real five-lap review. A report
    ///     that mints nothing passes even over a bag with no room left for the host's keys.
    /// </summary>
    [Fact]
    public async Task A_review_node_that_only_overwrites_existing_keys_is_never_capped()
    {
        var inner = new RecordingWorkflowStore(RunWith("review", Keys("standing", 16)));
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
    ///     Final review I3: a node that runs no lenses is checked too. The dispatcher counted its report against the
    ///     full cap, but a bag agents fill to sixteen has no room for <c>pr_url</c>, and Thalos checks that key only after
    ///     <c>open-pull-request</c> has pushed and opened the pull request. Red: checking projected nodes only, as before.
    /// </summary>
    [Fact]
    public async Task A_node_that_runs_no_lenses_may_not_take_the_room_kept_for_the_host()
    {
        var inner = new RecordingWorkflowStore(RunWith("implement", Keys("standing", 9)));
        var store = new ReviewHandoffWorkflowStore(inner, Definitions());

        await store.CompleteNodeAsync(inner.Run!.Id, 3, AnyTransition(), new NodeResult("changed", Report("fresh", 3)), CancellationToken.None);

        inner.Completed.Should().BeNull();
        inner.FailureMessage.Should().Contain("'implement'").And.Contain("to 17 with");
    }

    /// <summary>
    ///     Keys the bag already holds are counted once: a bag that holds the three mode keys and both host keys has no
    ///     further room to keep. Red: adding the reserved counts to the bag's instead of taking the union, which counts
    ///     them twice and refuses this report.
    /// </summary>
    [Fact]
    public async Task Host_keys_the_bag_already_holds_are_not_reserved_twice()
    {
        var inner = new RecordingWorkflowStore(RunWith(
            "implement",
            [.. Keys("standing", 9), "squad_mode", "pinning", "recall_tier", ReviewHandoff.PrUrlKey, ReviewHandoff.PublishErrorKey]));
        var store = new ReviewHandoffWorkflowStore(inner, Definitions());

        await store.CompleteNodeAsync(inner.Run!.Id, 3, AnyTransition(), new NodeResult("changed", Report("fresh", 2)), CancellationToken.None);

        inner.FailureMessage.Should().BeNull();
        inner.Completed.Should().NotBeNull();
    }

    /// <summary>
    ///     The host action is the reservation's beneficiary, not its subject: its report is checked by Thalos itself.
    ///     Red: checking every node, action nodes included, which refuses <c>pr_url</c> into a bag of fifteen.
    /// </summary>
    [Fact]
    public async Task The_publish_action_nodes_report_is_not_held_to_the_reservation()
    {
        var inner = new RecordingWorkflowStore(RunWith("publish", Keys("standing", 15)));
        var store = new ReviewHandoffWorkflowStore(inner, Definitions());

        await store.CompleteNodeAsync(
            inner.Run!.Id, 3, AnyTransition(),
            new NodeResult("published", new Dictionary<string, object?>(StringComparer.Ordinal) { [ReviewHandoff.PrUrlKey] = "https://x/pr/1" }),
            CancellationToken.None);

        inner.FailureMessage.Should().BeNull();
        inner.Completed!.Variables.Should().ContainKey(ReviewHandoff.PrUrlKey);
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
}
