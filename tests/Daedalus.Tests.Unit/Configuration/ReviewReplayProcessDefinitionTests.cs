using Thalos.Workflow;

namespace Daedalus.Tests.Unit.Configuration;

/// <summary>
///     Phase 2.10 task B7: pins the shape of <c>processes/review-replay.yaml</c>. The replay must run the very review node
///     <c>processes/manufacture.yaml</c> declares, so the reviewer setups it compares are judged on the same lenses, and
///     it must end after the review: nothing reachable from it publishes, opens a pull request or waits at the human
///     gate. Both files are read from the deployed output through the same <see cref="ProcessLoader"/> production uses.
/// </summary>
public sealed class ReviewReplayProcessDefinitionTests
{
    private static ProcessDefinition Load(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "processes", file);
        File.Exists(path).Should().BeTrue("processes/**/*.yaml must be a Content item in Daedalus.Api.csproj");

        var result = ProcessLoader.Load(File.ReadAllText(path));
        result.IsSuccess.Should().BeTrue(result.IsFailure ? $"processes/{file} must parse: {result.Error}" : "");
        return result.Value;
    }

    /// <summary>
    ///     Red, per assertion: rename the process in the file, and the name fails; change <c>version:</c>, and the
    ///     version fails (the store refuses a same-version content change, so the number is load-bearing).
    /// </summary>
    [Fact]
    public void The_process_is_review_replay_at_version_one()
    {
        var definition = Load("review-replay.yaml");

        definition.Name.Should().Be("review-replay");
        definition.Version.Should().Be(1);
    }

    /// <summary>
    ///     Red, per assertion: change the agent, the skill, the outcomes, the lens list or its order, or <c>maxVisits</c> in
    ///     the replay's file, and the matching assertion fails. The lens and agent assertions compare to the manufacture
    ///     file's own review node, so they also fail when only manufacture's node changes, which is the drift this guards.
    /// </summary>
    [Fact]
    public void The_review_node_is_pinned_like_manufactures_with_one_visit()
    {
        var replay = Load("review-replay.yaml").Nodes["review"];
        var manufacture = Load("manufacture.yaml").Nodes["review"];

        replay.Agent.Should().Be(manufacture.Agent);
        replay.Skill.Should().Be(manufacture.Skill);
        replay.Outcomes.Should().BeEquivalentTo(manufacture.Outcomes);
        replay.Lenses.Should().Equal(manufacture.Lenses);
        replay.Lenses.Should().Equal("correctness", "falsifiability", "mechanism");
        replay.MaxVisits.Should().Be(1);
    }

    /// <summary>
    ///     Red, per assertion: point <c>rejected</c> at any other node, and the branch fails; add an <c>implement</c>,
    ///     <c>gate</c> or <c>publish</c> node, and the node-set assertion fails; make <c>done</c> terminal as failed, and
    ///     the terminal assertion fails.
    /// </summary>
    [Fact]
    public void Both_outcomes_end_the_run_and_nothing_publishes_or_waits_for_a_human()
    {
        var definition = Load("review-replay.yaml");

        definition.Nodes["review"].Branch.Should().BeEquivalentTo(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["approved"] = "done",
            ["rejected"] = "done",
        });
        definition.Nodes.Keys.Should().BeEquivalentTo(["review", "done"]);
        definition.Nodes.Values.Should().OnlyContain(n => n.Action == null && n.Await == null);
        definition.Nodes["done"].Terminal.Should().Be("succeeded");
    }
}
