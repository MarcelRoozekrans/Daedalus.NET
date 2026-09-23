using Daedalus.Agents.Workflow;

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
}
