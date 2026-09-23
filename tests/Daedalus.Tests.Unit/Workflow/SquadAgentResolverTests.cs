using Daedalus.Agents.Workflow;
using Thalos;
using Thalos.Workflow;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     <see cref="SquadAgentResolver"/> in isolation: the flag decides whether a role resolves to itself or to
///     one shared fallback. <see cref="Configuration.SquadConfigurationDriftTests"/> covers the same behaviour
///     wired through the real <c>Daedalus.Api</c> configuration and DI container.
/// </summary>
public sealed class SquadAgentResolverTests
{
    [Fact]
    public void Disabled_squad_resolves_every_role_to_the_fallback_agent()
    {
        var resolver = new SquadAgentResolver(new SquadOptions { Enabled = false, FallbackAgentName = "Daedalus Architect" });

        resolver.Resolve("implementer").Should().Be("Daedalus Architect");
        resolver.Resolve("reviewer").Should().Be("Daedalus Architect");
    }

    [Fact]
    public void Enabled_squad_resolves_a_role_to_itself()
    {
        var resolver = new SquadAgentResolver(new SquadOptions { Enabled = true, FallbackAgentName = "Daedalus Architect" });

        resolver.Resolve("implementer").Should().Be("implementer");
        resolver.Resolve("reviewer").Should().Be("reviewer");
    }

    [Fact]
    public void Null_constructor_argument_is_rejected()
    {
        var act = () => new SquadAgentResolver(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_role_name_is_rejected(string? roleName)
    {
        var resolver = new SquadAgentResolver(new SquadOptions { Enabled = true, FallbackAgentName = "Daedalus Architect" });

        var act = () => resolver.Resolve(roleName!);

        act.Should().Throw<ArgumentException>();
    }

    /// <summary>
    ///     The wiring half. <see cref="SquadWorkflowReferenceResolver"/> is what makes the flag change which
    ///     agent a process node is dispatched as; without it <see cref="SquadAgentResolver"/> is a pure function
    ///     nothing calls, which is exactly what task B4 left behind and said so.
    /// </summary>
    [Theory]
    [InlineData(true, "implementer")]
    [InlineData(true, "reviewer")]
    [InlineData(false, "implementer")]
    [InlineData(false, "reviewer")]
    public async Task The_workflow_resolver_looks_up_the_mapped_name_not_the_declared_one(bool squadEnabled, string roleName)
    {
        var inner = Substitute.For<IWorkflowReferenceResolver>();
        inner.ResolveAgentIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<AgentId?>((AgentId?)null));

        var resolver = new SquadWorkflowReferenceResolver(
            inner, new SquadAgentResolver(new SquadOptions { Enabled = squadEnabled, FallbackAgentName = "Daedalus Architect" }));

        await resolver.ResolveAgentIdAsync(roleName, CancellationToken.None);

        var expected = squadEnabled ? roleName : "Daedalus Architect";
        await inner.Received(1).ResolveAgentIdAsync(expected, Arg.Any<CancellationToken>());
        if (!squadEnabled)
        {
            await inner.DidNotReceive().ResolveAgentIdAsync(roleName, Arg.Any<CancellationToken>());
        }
    }

    /// <summary>
    ///     A node's <c>skill:</c> pin names a document, not an agent, and the squad flag has no opinion about
    ///     it. Remapping it would send every node to the fallback's skill and break the pipeline in both modes.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_workflow_resolver_never_remaps_a_skill_name(bool squadEnabled)
    {
        var inner = Substitute.For<IWorkflowReferenceResolver>();
        inner.SkillExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<bool>(true));

        var resolver = new SquadWorkflowReferenceResolver(
            inner, new SquadAgentResolver(new SquadOptions { Enabled = squadEnabled, FallbackAgentName = "Daedalus Architect" }));

        await resolver.SkillExistsAsync("manufacture-review", CancellationToken.None);

        await inner.Received(1).SkillExistsAsync("manufacture-review", Arg.Any<CancellationToken>());
    }
}
