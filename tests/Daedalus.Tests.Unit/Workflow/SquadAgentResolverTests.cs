using System.Collections.Concurrent;
using Daedalus.Agents.Workflow;
using Microsoft.Extensions.Logging;
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
            inner, new SquadAgentResolver(new SquadOptions { Enabled = squadEnabled, FallbackAgentName = "Daedalus Architect" }), new CapturingLogger());

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
            inner, new SquadAgentResolver(new SquadOptions { Enabled = squadEnabled, FallbackAgentName = "Daedalus Architect" }), new CapturingLogger());

        await resolver.SkillExistsAsync("manufacture-review", CancellationToken.None);

        await inner.Received(1).SkillExistsAsync("manufacture-review", Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     The diagnosability half of the final review's finding 2. Nothing downstream of this resolver knows
    ///     the squad remapped the name: <c>ProcessValidator</c> reports the name it read out of the process
    ///     file, and <c>WorkflowNodeDispatcher.ResolveAgentAsync</c> fails the run naming
    ///     <c>ProcessNode.Agent</c>. With the squad off, both send a reader to
    ///     <c>processes/manufacture.yaml</c> — which is correct and therefore a dead end — for a fault that
    ///     lives in <c>Thalos:Squad:FallbackAgentName</c>. This resolver is the only place holding both names.
    /// </summary>
    [Fact]
    public async Task A_missing_fallback_agent_is_reported_under_the_name_that_was_looked_up()
    {
        var inner = Substitute.For<IWorkflowReferenceResolver>();
        inner.ResolveAgentIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<AgentId?>((AgentId?)null));
        var logger = new CapturingLogger();

        var resolver = new SquadWorkflowReferenceResolver(
            inner, new SquadAgentResolver(new SquadOptions { Enabled = false, FallbackAgentName = "Renamed Architect" }), logger);

        var id = await resolver.ResolveAgentIdAsync("implementer", CancellationToken.None);

        id.Should().BeNull("the decorator reports the miss, it does not invent an agent");

        // Both names, and the key to look at. Falsifiable per clause: dropping either placeholder from the
        // LoggerMessage template, or logging the declared role in the resolved slot, turns one of these red.
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Error);
        entry.Message.Should().Contain("Renamed Architect", "the name actually looked up is the one nothing else reports");
        entry.Message.Should().Contain("implementer", "and the declared role is what ties the message back to the process file");
        entry.Message.Should().Contain("Thalos:Squad:FallbackAgentName", "the message has to name the key that is wrong");
    }

    /// <summary>
    ///     With the squad on, a role resolves to itself, so the run's own error already names the right thing
    ///     and a second message here would be noise that adds nothing.
    /// </summary>
    [Fact]
    public async Task A_role_that_was_not_remapped_is_left_to_the_callers_own_error()
    {
        var inner = Substitute.For<IWorkflowReferenceResolver>();
        inner.ResolveAgentIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<AgentId?>((AgentId?)null));
        var logger = new CapturingLogger();

        var resolver = new SquadWorkflowReferenceResolver(
            inner, new SquadAgentResolver(new SquadOptions { Enabled = true, FallbackAgentName = "Daedalus Architect" }), logger);

        await resolver.ResolveAgentIdAsync("implementer", CancellationToken.None);

        logger.Entries.Should().BeEmpty();
    }

    /// <summary>Records what was logged, so the two names in the message can be asserted apart.</summary>
    private sealed class CapturingLogger : ILogger<SquadWorkflowReferenceResolver>
    {
        public ConcurrentBag<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
