using Daedalus.Agents.Workflow;
using Thalos.Workflow;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Guards the one fact <see cref="ReviewHandoffWorkflowStore"/> and <see cref="StandingInstructionsRunner"/>
///     both key off but neither one enforces itself: the shipped <c>processes/manufacture.yaml</c> pins its
///     <c>retrospect</c> node to the exact skill name <see cref="ReviewHandoff.RetrospectSkillName"/> names. A
///     process author who renamed the node's <c>skill:</c> — even to something that still parses and validates —
///     would silently stop the retrospect projection and the standing-instructions block from ever applying to
///     that node, with no error anywhere: <c>ReviewHandoffWorkflowStore.ProjectionForAsync</c> would answer
///     <see langword="null"/> instead of <see cref="ReviewHandoff.RetrospectReads"/>, and the node would receive
///     the implementer's whole narrative — exactly the leak the projection exists to prevent.
/// </summary>
/// <remarks>
///     <see cref="Daedalus.Tests.Unit.Configuration.ManufactureProcessDefinitionTests"/> is the neighbouring
///     guard and covers the graph's shape more broadly (agent, outcomes, branches). This one is narrower and
///     asserts the one string two independent components in <c>Daedalus.Agents</c> both read off the process
///     file and must agree with, which is why it lives beside the mechanism as its own small file rather than as
///     one more fact folded into that broader suite.
/// </remarks>
public sealed class ProcessDefinitionDriftTests
{
    private static ProcessDefinition LoadManufactureProcess()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "processes", "manufacture.yaml");
        File.Exists(path).Should().BeTrue("processes/**/*.yaml must be a Content item in Daedalus.Api.csproj");

        var result = ProcessLoader.Load(File.ReadAllText(path));
        result.IsSuccess.Should().BeTrue(result.IsFailure ? $"processes/manufacture.yaml must parse: {result.Error}" : "");
        return result.Value;
    }

    /// <summary>
    ///     Falsifiable: renaming <c>retrospect</c>'s <c>skill:</c> in <c>processes/manufacture.yaml</c> — to
    ///     anything other than <see cref="ReviewHandoff.RetrospectSkillName"/> — turns this red without touching
    ///     a single line of C#. Renaming <see cref="ReviewHandoff.RetrospectSkillName"/> itself turns it red the
    ///     other way, which is the point: the two are only in agreement because this test checks them against
    ///     each other rather than each restating a literal.
    /// </summary>
    [Fact]
    public void The_shipped_retrospect_node_uses_the_skill_the_projection_keys_on()
    {
        var process = LoadManufactureProcess();

        process.Nodes["retrospect"].Skill.Should().Be(ReviewHandoff.RetrospectSkillName);
        process.Nodes["retrospect"].Agent.Should().Be("reviewer");
    }
}
