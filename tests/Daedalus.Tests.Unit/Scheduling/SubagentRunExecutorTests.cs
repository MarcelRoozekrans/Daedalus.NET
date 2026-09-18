using Daedalus.Agents.Scheduling;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Thalos;

namespace Daedalus.Tests.Unit.Scheduling;

/// <summary>
///     Covers <see cref="SubagentRunExecutor"/>: the only type in Daedalus permitted to touch
///     <see cref="ISubagentRunner"/>. Every failure assertion here checks <see cref="AgentError.Message"/> as well
///     as <see cref="AgentError.Code"/> — <see cref="AgentErrorCode.Validation"/> is enum member 0, so a
///     Code-only assertion would pass against a <see langword="default"/>(<see cref="AgentError"/>) that no
///     production code path ever produced.
/// </summary>
public sealed class SubagentRunExecutorTests
{
    private static readonly AgentId ScoutId = new(new Guid(0x7c2a1f40, 0x9b3e, 0x4d58, 0x8a, 0x16, 0x2e, 0x5c, 0x9f, 0x0b, 0x4d, 0x71));

    [Fact]
    public async Task A_successful_run_returns_the_turn_text()
    {
        var runner = Substitute.For<ISubagentRunner>();
        runner.RunAsync(Arg.Any<SubagentRunRequest>(), Arg.Any<CancellationToken>())
              .Returns(ZeroAlloc.Results.Result<AgentTurnResult, AgentError>.Success(TurnWith("three open PRs")));

        var result = await Executor(runner).RunAsync("scout", "sweep", "schedule:daedalus", ["reader"], default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("three open PRs");
    }

    [Fact]
    public async Task A_failed_run_returns_the_error_and_does_not_throw()
    {
        // a throw would escape into the outbox dispatcher as an infrastructure fault and burn eight
        // retries re-running the same failing turn; an AgentError is an outcome the caller reports
        var runner = Substitute.For<ISubagentRunner>();
        runner.RunAsync(Arg.Any<SubagentRunRequest>(), Arg.Any<CancellationToken>())
              .Returns(ZeroAlloc.Results.Result<AgentTurnResult, AgentError>.Failure(AgentError.ProviderError("529", "overloaded")));

        var result = await Executor(runner).RunAsync("scout", "sweep", "schedule:daedalus", ["reader"], default);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(AgentErrorCode.ProviderError);
        result.Error.Message.Should().Be("529");
    }

    [Fact]
    public async Task The_run_executes_as_the_supplied_detached_principal_not_a_human()
    {
        // spec D7 and §4, made falsifiable
        var runner = Substitute.For<ISubagentRunner>();
        SubagentRunRequest? captured = null;
        runner.RunAsync(Arg.Do<SubagentRunRequest>(r => captured = r), Arg.Any<CancellationToken>())
              .Returns(ZeroAlloc.Results.Result<AgentTurnResult, AgentError>.Success(TurnWith("ok")));

        await Executor(runner).RunAsync("scout", "sweep", "schedule:daedalus", ["reader"], default);

        captured!.Caller.Id.Should().Be("schedule:daedalus");
        captured.Caller.Roles.Should().BeEquivalentTo(["reader"]);
        captured.ParentSessionId.Should().BeNull("a scheduled run has no parent turn");
        captured.Depth.Should().Be(0);
    }

    [Fact]
    public async Task The_configured_budget_is_applied_to_every_run()
    {
        var runner = Substitute.For<ISubagentRunner>();
        SubagentRunRequest? captured = null;
        runner.RunAsync(Arg.Do<SubagentRunRequest>(r => captured = r), Arg.Any<CancellationToken>())
              .Returns(ZeroAlloc.Results.Result<AgentTurnResult, AgentError>.Success(TurnWith("ok")));

        await Executor(runner, maxTokens: 50_000, deadlineSeconds: 300)
            .RunAsync("scout", "sweep", "schedule:daedalus", ["reader"], default);

        captured!.Budget.Should().Be(new SubagentBudget(50_000, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task An_unknown_agent_name_returns_a_failure_naming_the_agent_rather_than_throwing()
    {
        var result = await Executor(Substitute.For<ISubagentRunner>())
            .RunAsync("nope", "sweep", "schedule:daedalus", ["reader"], default);

        result.IsFailure.Should().BeTrue();
        // asserting on Message as well as Code is deliberate: AgentErrorCode.Validation is member 0, so
        // a Code-only assertion passes against a default(AgentError) that no code ever produced
        result.Error.Message.Should().Contain("nope");
    }

    [Fact]
    public async Task Agent_names_resolve_case_insensitively()
    {
        // agent names are typed by humans on phones; ChannelPump already resolves them this way
        var runner = Substitute.For<ISubagentRunner>();
        runner.RunAsync(Arg.Any<SubagentRunRequest>(), Arg.Any<CancellationToken>())
              .Returns(ZeroAlloc.Results.Result<AgentTurnResult, AgentError>.Success(TurnWith("ok")));

        var result = await Executor(runner).RunAsync("SCOUT", "sweep", "schedule:daedalus", ["reader"], default);

        result.IsSuccess.Should().BeTrue();
    }

    private static SubagentRunExecutor Executor(ISubagentRunner runner, int maxTokens = 50_000, int deadlineSeconds = 300)
    {
        var catalog = new FakeAgentCatalog(new AgentDefinition { Id = ScoutId, Name = "scout", Instructions = "irrelevant" });
        var options = Options.Create(new DetachedRunOptions
        {
            PrincipalId = "schedule:daedalus",
            Roles = ["reader"],
            MaxTotalTokens = maxTokens,
            DeadlineSeconds = deadlineSeconds,
        });

        return new SubagentRunExecutor(runner, catalog, options, NullLogger<SubagentRunExecutor>.Instance);
    }

    private static AgentTurnResult TurnWith(string text) =>
        new(TurnId.New(), new SessionId(Guid.Empty), text, default, [], TimeSpan.Zero);

    /// <summary>A minimal <see cref="IAgentCatalog"/> test double naming exactly the agents given to its constructor.</summary>
    private sealed class FakeAgentCatalog(params AgentDefinition[] agents) : IAgentCatalog
    {
        public IReadOnlyList<AgentDefinition> Agents { get; } = agents;

        public bool TryGet(AgentId id, out AgentDefinition definition)
        {
            var match = Agents.FirstOrDefault(a => a.Id == id);
            definition = match!;
            return match is not null;
        }
    }
}
