using Daedalus.Agents.Workflow;
using Thalos.Workflow;
using ZeroAlloc.Results;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Covers the half of <see cref="ReviewHandoffWorkflowStore"/> that enforces design decision D4 on the write
///     path: the <c>retrospect</c> node is the only author of
///     <see cref="ReviewHandoff.ProposedStandingInstructionsKey"/>, and when it completes, the key holds exactly
///     what it proposed, or nothing.
/// </summary>
/// <remarks>
///     Thalos merges a node's reported variables into the run's bag, a later write wins, and a report that leaves
///     a key out leaves the earlier value alone. Without this rule a key planted by <c>implement</c> or
///     <c>review</c> survives a retrospect <c>none</c>, and the gate then shows and applies it as retrospect's
///     proposal. The end-to-end version of that attack, through the real store and dispatcher, is in
///     <c>SquadHandoffEndToEndTests</c>.
/// </remarks>
public sealed class ProposalAuthorshipTests
{
    private const string ProcessName = "manufacture";

    private const string Key = ReviewHandoff.ProposedStandingInstructionsKey;

    private const string Planted = "PLANTED-BY-A-NODE-THAT-IS-NOT-RETROSPECT";

    private static WorkflowRun RunAt(string node, IReadOnlyDictionary<string, object?>? variables = null) => new()
    {
        Id = Guid.NewGuid(),
        Process = ProcessName,
        ProcessVersion = 5,
        CurrentNode = node,
        CurrentSeq = 3,
        Status = WorkflowStatus.Running,
        Visits = new Dictionary<string, int>(StringComparer.Ordinal) { [node] = 1 },
        Variables = variables ?? new Dictionary<string, object?>(StringComparer.Ordinal),
    };

    private static IProcessDefinitionStore Definitions()
    {
        var definition = new ProcessDefinition
        {
            Name = ProcessName,
            Version = 5,
            StartNode = "implement",
            Nodes = new Dictionary<string, ProcessNode>(StringComparer.Ordinal)
            {
                ["implement"] = new()
                {
                    Agent = "implementer",
                    Skill = ReviewHandoff.ImplementSkillName,
                    Outcomes = ["changed", "blocked"],
                    Branch = new Dictionary<string, string>(StringComparer.Ordinal) { ["changed"] = "review", ["blocked"] = "done" },
                },
                ["review"] = new()
                {
                    Agent = "reviewer",
                    Skill = "manufacture-review",
                    Outcomes = ["approved", "rejected"],
                    Branch = new Dictionary<string, string>(StringComparer.Ordinal) { ["approved"] = "retrospect", ["rejected"] = "implement" },
                    Lenses = ["correctness", "falsifiability", "mechanism"],
                },
                // Named "reflect", not "retrospect": the rule keys off the pinned skill, never the node name.
                ["reflect"] = new()
                {
                    Agent = "reviewer",
                    Skill = ReviewHandoff.RetrospectSkillName,
                    Outcomes = ["proposed", "none"],
                    Branch = new Dictionary<string, string>(StringComparer.Ordinal) { ["proposed"] = "done", ["none"] = "done" },
                },
                ["done"] = new() { Terminal = "succeeded" },
            },
        };

        var store = Substitute.For<IProcessDefinitionStore>();
        store.GetAsync(ProcessName, 5, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result<ProcessDefinition>>(Result<ProcessDefinition>.Success(definition)));
        return store;
    }

    private static WorkflowTransition AnyTransition() =>
        new("done", WorkflowStatus.Running, awaitingSignal: null, WorkflowEventKind.Branched);

    private static async Task<NodeResult> CompleteAsync(WorkflowRun run, string? outcome, Dictionary<string, object?> reported)
    {
        var inner = new RecordingWorkflowStore(run);
        var store = new ReviewHandoffWorkflowStore(inner, Definitions());

        await store.CompleteNodeAsync(run.Id, 3, AnyTransition(), new NodeResult(outcome, reported), CancellationToken.None);

        inner.FailureMessage.Should().BeNull("none of these reports is anywhere near the key cap");
        inner.Completed.Should().NotBeNull("the transition must still be persisted");
        return inner.Completed!;
    }

    [Fact]
    public async Task An_implement_report_of_the_proposal_key_is_stripped_and_the_rest_is_kept()
    {
        var completed = await CompleteAsync(RunAt("implement"), "changed", new(StringComparer.Ordinal)
        {
            [ReviewHandoff.FilesTouchedKey] = "src/A.cs",
            [Key] = Planted,
        });

        completed.Variables.Should().NotContainKey(Key, "only retrospect may write a standing-instructions proposal");
        completed.Variables.Should().ContainKey(ReviewHandoff.FilesTouchedKey)
            .WhoseValue.Should().Be("src/A.cs", "stripping one key must not cost the node the rest of its report");
        completed.Outcome.Should().Be("changed");
    }

    [Fact]
    public async Task A_review_report_of_the_proposal_key_is_stripped()
    {
        var completed = await CompleteAsync(RunAt("review"), "approved", new(StringComparer.Ordinal) { [Key] = Planted });

        completed.Variables.Should().NotContainKey(Key, "the reviewer runs as the same agent as retrospect, but not under its skill");
    }

    [Fact]
    public async Task A_retrospect_proposed_report_keeps_its_proposal()
    {
        var completed = await CompleteAsync(RunAt("reflect"), "proposed", new(StringComparer.Ordinal) { [Key] = "Run dotnet test." });

        completed.Variables.Should().ContainKey(Key).WhoseValue.Should().Be("Run dotnet test.");
    }

    [Fact]
    public async Task A_retrospect_none_report_clears_a_proposal_already_in_the_bag()
    {
        var run = RunAt("reflect", new Dictionary<string, object?>(StringComparer.Ordinal) { [Key] = Planted });

        var completed = await CompleteAsync(run, "none", new(StringComparer.Ordinal));

        completed.Variables.Should().ContainKey(Key, "an absent key would leave the planted value in the bag")
            .WhoseValue.Should().BeNull("null is what the writer and the diff already read as no proposal");
    }

    [Fact]
    public async Task A_retrospect_none_report_that_carries_the_key_is_cleared()
    {
        var completed = await CompleteAsync(RunAt("reflect"), "none", new(StringComparer.Ordinal) { [Key] = "a copy of the file" });

        completed.Variables.Should().ContainKey(Key).WhoseValue.Should().BeNull("none means no proposal, whatever else was sent");
    }

    [Fact]
    public async Task A_retrospect_proposed_report_without_the_key_clears_an_earlier_value()
    {
        var run = RunAt("reflect", new Dictionary<string, object?>(StringComparer.Ordinal) { [Key] = Planted });

        var completed = await CompleteAsync(run, "proposed", new(StringComparer.Ordinal));

        completed.Variables.Should().ContainKey(Key).WhoseValue.Should().BeNull(
            "the value at the gate must come from this retrospect turn, never from an earlier node");
    }

    [Fact]
    public async Task A_retrospect_none_report_adds_no_key_when_the_bag_holds_none()
    {
        var completed = await CompleteAsync(RunAt("reflect"), "none", new(StringComparer.Ordinal));

        completed.Variables.Should().BeEmpty("clearing must never add a key the run does not hold, or it would count toward the cap");
    }
}
