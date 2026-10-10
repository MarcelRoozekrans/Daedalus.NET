using System.Globalization;
using System.Reflection;
using Daedalus.Agents.Workflow;
using Thalos;
using Thalos.Workflow;
using ZeroAlloc.Results;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Covers the half of <see cref="ReviewHandoffWorkflowStore"/> that enforces Thalos' sixteen-key cap on the
///     write path, and keeps room in every agent node's count for the keys the host adds on every transition and the
///     ones <c>open-pull-request</c> writes. The split is: a review node's reported variables are dropped before any
///     count (nothing reads them), and the cap is enforced on every node that keeps its variables, including a
///     projected <c>retrospect</c> node. Narrowing the bag a dispatch is built from also narrows what
///     <c>WorkflowNodeDispatcher.BuildNodeResult</c> counts that node's report against, so on a projected node the
///     dispatcher's own count stops binding; the store counts against the run's real bag instead.
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
                ["retrospect"] = new()
                {
                    Agent = "reviewer",
                    Skill = ReviewHandoff.RetrospectSkillName,
                    Outcomes = ["none", "proposed"],
                    Branch = new Dictionary<string, string>(StringComparer.Ordinal) { ["none"] = "done", ["proposed"] = "done" },
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
    ///     The shape the finding describes: an agent node whose real bag is already near the cap reports fresh
    ///     keys that take it past. The dispatcher could not have caught this where the bag is projected. Since the
    ///     final review's I3, the count includes the three keys WorkflowRunModeStore adds and the two open-pull-request
    ///     writes: ten keys plus two fresh ones is twelve, and seventeen with those five.
    ///     <para>
    ///     This used to run on the review node. A review node's reported variables are now dropped before the cap
    ///     check (see <see cref="A_review_node_that_reports_variables_over_a_full_bag_completes_and_the_store_receives_none"/>),
    ///     so the cap is exercised here on <c>implement</c>, where variables are kept. The real-bag count is pinned on
    ///     the projected retrospect node, see
    ///     <see cref="A_retrospect_report_is_counted_against_the_real_bag_not_the_projected_one"/>.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task An_implement_node_report_that_takes_the_real_bag_past_the_cap_still_fails_the_run()
    {
        var inner = new RecordingWorkflowStore(RunWith("implement", Keys("standing", 10)));
        var store = new ReviewHandoffWorkflowStore(inner, Definitions(), TestNodeUsage.Recorder());

        await store.CompleteNodeAsync(inner.Run!.Id, 3, AnyTransition(), new NodeResult("changed", Report("fresh", 2)), CancellationToken.None);

        // Falsifiable: deleting the cap check turns this red. implement is not projected, so counting a projected bag
        // cannot turn it red; the retrospect test below pins that.
        inner.Completed.Should().BeNull("a report over the cap must not be persisted as a transition");
        inner.FailureMessage.Should().NotBeNull();
        inner.FailureMessage.Should().Contain("to 12 distinct keys", "the message must name the count the report takes the bag to");
        inner.FailureMessage.Should().Contain("to 17 with", "and the count with the host's keys");
        inner.FailureMessage.Should().Contain("limit of 16", "and the limit it passes");
        inner.FailureMessage.Should().Contain("pr_url", "and name the room it keeps");
        inner.FailureMessage.Should().Contain("'implement'", "and the node whose report was rejected");
    }

    /// <summary>
    ///     The mirror assertion, without which the one above would be satisfied by a decorator that failed
    ///     every transition: nine keys plus two fresh ones plus the host's five is exactly sixteen.
    /// </summary>
    [Fact]
    public async Task An_implement_node_report_that_stays_inside_the_cap_is_completed_normally()
    {
        var inner = new RecordingWorkflowStore(RunWith("implement", Keys("standing", 9)));
        var store = new ReviewHandoffWorkflowStore(inner, Definitions(), TestNodeUsage.Recorder());

        await store.CompleteNodeAsync(inner.Run!.Id, 3, AnyTransition(), new NodeResult("changed", Report("fresh", 2)), CancellationToken.None);

        inner.FailureMessage.Should().BeNull();
        inner.Completed.Should().NotBeNull();
        inner.Completed!.Outcome.Should().Be("changed");
        inner.Completed.Variables.Should().HaveCount(2, "the decorator checks the report, and rewrites it only to enforce authorship");
    }

    /// <summary>
    ///     Live run e973b5e7: the reviewer's final outcome call carried six free-form variables on a bag of nine, and
    ///     the run failed at the cap. Nothing reads a review node's variables, because review evidence lives in
    ///     host-written records, so the host drops them and the outcome passes through untouched.
    ///     Also pins that a review node cannot land a host-owned key: the forged <c>pr_url</c> and <c>run_mode</c> never
    ///     reach the inner store.
    ///     Red: pass the reported variables through to the cap check and the inner store.
    /// </summary>
    [Fact]
    public async Task A_review_node_that_reports_variables_over_a_full_bag_completes_and_the_store_receives_none()
    {
        var inner = new RecordingWorkflowStore(RunWith("review", Keys("standing", 9)));
        var store = new ReviewHandoffWorkflowStore(inner, Definitions(), TestNodeUsage.Recorder());
        var usage = new TurnUsage(1000, 50, "m") { CacheReadTokens = 800 };

        var reported = Report("fresh", 6);
        reported[ReviewHandoff.PrUrlKey] = "https://forged/pr/1";
        reported["run_mode"] = "forged";

        await store.CompleteNodeAsync(
            inner.Run!.Id, 3, AnyTransition(), new NodeResult("rejected", reported) { Usage = usage }, CancellationToken.None);

        inner.FailureMessage.Should().BeNull("the reported variables are dropped before the cap is checked");
        inner.Completed.Should().NotBeNull();
        inner.Completed!.Outcome.Should().Be("rejected", "the outcome passes through unchanged");
        inner.Completed.Usage.Should().Be(usage, "so does the usage");
        inner.Completed.Variables.Should().BeEmpty("no review variable reaches the inner store");
    }

    /// <summary>
    ///     The cap is counted against the run's real bag, <c>Inner</c>'s, not the projected bag this store's own
    ///     FindAsync hands the dispatcher. Retrospect is projected to <see cref="ReviewHandoff.RetrospectReads"/> and keeps
    ///     its variables: the real bag holds ten keys, so two fresh ones are twelve and seventeen with the host's five,
    ///     while the one-key projected bag would count only eight.
    ///     Red: count this.FindAsync's projected bag instead of Inner's.
    /// </summary>
    [Fact]
    public async Task A_retrospect_report_is_counted_against_the_real_bag_not_the_projected_one()
    {
        var inner = new RecordingWorkflowStore(RunWith("retrospect", [.. Keys("standing", 9), ReviewHandoff.LearningsKey]));
        var store = new ReviewHandoffWorkflowStore(inner, Definitions(), TestNodeUsage.Recorder());

        await store.CompleteNodeAsync(inner.Run!.Id, 3, AnyTransition(), new NodeResult("none", Report("fresh", 2)), CancellationToken.None);

        inner.Completed.Should().BeNull("a report over the cap on the real bag must not be persisted");
        inner.FailureMessage.Should().NotBeNull();
        inner.FailureMessage.Should().Contain("to 12 distinct keys");
        inner.FailureMessage.Should().Contain("to 17 with");
        inner.FailureMessage.Should().Contain("limit of 16");
        inner.FailureMessage.Should().Contain("'retrospect'");
    }

    /// <summary>
    ///     Retrospect declares no lenses, so the review-node drop does not apply to it and its variables are kept.
    ///     Red: dropping variables for every node.
    /// </summary>
    [Fact]
    public async Task A_retrospect_nodes_reported_variables_still_reach_the_store_unchanged()
    {
        var inner = new RecordingWorkflowStore(RunWith("retrospect", Keys("standing", 3)));
        var store = new ReviewHandoffWorkflowStore(inner, Definitions(), TestNodeUsage.Recorder());
        var reported = Report("fresh", 2);

        await store.CompleteNodeAsync(inner.Run!.Id, 3, AnyTransition(), new NodeResult("none", reported), CancellationToken.None);

        inner.FailureMessage.Should().BeNull();
        inner.Completed!.Variables.Should().BeEquivalentTo(reported);
    }

    /// <summary>
    ///     Overwriting a key the run already holds never moves the distinct count - the rule that lets a capped
    ///     loop overwrite the same few keys lap after lap. Stated as its own assertion because a check written
    ///     as "bag count plus report count" would pass both tests above and fail every real five-lap review. A report
    ///     that mints nothing passes even over a bag with no room left for the host's keys.
    /// </summary>
    [Fact]
    public async Task An_implement_node_that_only_overwrites_existing_keys_is_never_capped()
    {
        var inner = new RecordingWorkflowStore(RunWith("implement", Keys("standing", 16)));
        var store = new ReviewHandoffWorkflowStore(inner, Definitions(), TestNodeUsage.Recorder());

        var overwrite = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["standing0"] = "new value",
            ["standing1"] = "new value",
        };
        await store.CompleteNodeAsync(inner.Run!.Id, 3, AnyTransition(), new NodeResult("changed", overwrite), CancellationToken.None);

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
        var store = new ReviewHandoffWorkflowStore(inner, Definitions(), TestNodeUsage.Recorder());

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
        var store = new ReviewHandoffWorkflowStore(inner, Definitions(), TestNodeUsage.Recorder());

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
        var store = new ReviewHandoffWorkflowStore(inner, Definitions(), TestNodeUsage.Recorder());

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
