using Daedalus.Agents;
using Daedalus.Agents.Scheduling;
using Daedalus.Agents.Workflow;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Thalos;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Covers <see cref="BudgetedSubagentRunner"/>: the decorator that stamps <see cref="SubagentRunRequest.Budget"/>
///     before every workflow-run agent turn: the token ceiling from the agent's own entry in
///     <see cref="TurnBudgetsConfig"/>, the deadline from <see cref="DetachedRunOptions"/>. Without it, a request built by
///     Thalos' <c>WorkflowNodeDispatcher</c> — which never sets a budget itself — falls through to Thalos' own
///     global <c>SubagentOptions.DefaultBudget</c> instead of Daedalus' configured ceiling; these tests go red if
///     the decorator is removed or stops stamping.
/// </summary>
public sealed class BudgetedSubagentRunnerTests
{
    private static readonly AgentId AnyAgentId = new(new Guid(0x7c2a1f40, 0x9b3e, 0x4d58, 0x8a, 0x16, 0x2e, 0x5c, 0x9f, 0x0b, 0x4d, 0x71));

    private static readonly AgentId ImplementerId = new(new Guid(0x1a2b3c4d, 0x0001, 0x4000, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01));
    private static readonly AgentId ReviewerId = new(new Guid(0x1a2b3c4d, 0x0002, 0x4000, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02));
    private static readonly AgentId ArchitectId = new(new Guid(0x1a2b3c4d, 0x0003, 0x4000, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03));
    private static readonly AgentId CapitalisedReviewerId = new(new Guid(0x1a2b3c4d, 0x0004, 0x4000, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04));

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(300);

    /// <summary>
    ///     The implementer and the reviewer each get the ceiling their own <c>TurnBudgets:Agents</c> entry names, not the
    ///     scout's <c>DetachedRuns:MaxTotalTokens</c>, with the deadline still taken from <c>DetachedRuns</c>.
    ///     <para>Red: stamp <c>DetachedRuns.MaxTotalTokens</c> instead of the per-agent ceiling; both read 150000.</para>
    /// </summary>
    [Fact]
    public async Task Each_agent_gets_its_own_ceiling()
    {
        var implementer = await BudgetFor(ImplementerId);
        var reviewer = await BudgetFor(ReviewerId);

        implementer.Should().Be(new SubagentBudget(875_000, Deadline));
        reviewer.Should().Be(new SubagentBudget(600_000, Deadline));
    }

    /// <summary>
    ///     An agent the catalog knows but <c>TurnBudgets:Agents</c> does not list runs under <c>Default</c>.
    ///     <para>Red: return 0, or the scout's value, for an unlisted name.</para>
    /// </summary>
    [Fact]
    public async Task An_unlisted_agent_gets_the_default()
    {
        (await BudgetFor(ArchitectId)).Should().Be(new SubagentBudget(875_000, Deadline));
    }

    /// <summary>
    ///     An agent the catalog cannot serve still gets a budget, the default one: the turn goes on, and Thalos reports the
    ///     missing agent itself.
    ///     <para>Red: throw, or skip the stamp, when <c>TryGet</c> returns false.</para>
    /// </summary>
    [Fact]
    public async Task An_agent_missing_from_the_catalog_gets_the_default()
    {
        (await BudgetFor(AnyAgentId)).Should().Be(new SubagentBudget(875_000, Deadline));
    }

    /// <summary>
    ///     Agent names match their <c>TurnBudgets:Agents</c> key case-insensitively, through the real configuration binder
    ///     rather than a hand-built dictionary.
    ///     <para>Red: build <c>Agents</c> with the default comparer; "Reviewer" misses and reads the default 875000.</para>
    /// </summary>
    [Fact]
    public async Task The_agent_name_matches_case_insensitively()
    {
        (await BudgetFor(CapitalisedReviewerId)).Should().Be(new SubagentBudget(600_000, Deadline));
    }

    /// <summary>
    ///     A request that already carries a budget keeps it; the configured ceiling stamps only a request without one.
    ///     <para>Red: stamp unconditionally; the budget reads the agent's ceiling.</para>
    /// </summary>
    [Fact]
    public async Task A_request_that_already_names_a_budget_is_passed_through_unchanged()
    {
        var inner = Substitute.For<ISubagentRunner>();
        SubagentRunRequest? captured = null;
        inner.RunAsync(Arg.Do<SubagentRunRequest>(r => captured = r), Arg.Any<CancellationToken>())
             .Returns(ZeroAlloc.Results.Result<AgentTurnResult, AgentError>.Success(TurnWith("ok")));

        var runner = Runner(inner);
        var explicitBudget = new SubagentBudget(1_234, TimeSpan.FromSeconds(7));
        var request = RequestWithoutBudget() with { Budget = explicitBudget };

        await runner.RunAsync(request, default);

        captured!.Budget.Should().Be(explicitBudget, "a caller-supplied budget always wins over the configured default");
    }

    [Fact]
    public async Task The_inner_runners_result_is_returned_unchanged()
    {
        var inner = Substitute.For<ISubagentRunner>();
        inner.RunAsync(Arg.Any<SubagentRunRequest>(), Arg.Any<CancellationToken>())
             .Returns(ZeroAlloc.Results.Result<AgentTurnResult, AgentError>.Success(TurnWith("the answer")));

        var result = await Runner(inner).RunAsync(RequestWithoutBudget(), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Text.Should().Be("the answer");
    }

    private static async Task<SubagentBudget?> BudgetFor(AgentId agent)
    {
        var inner = Substitute.For<ISubagentRunner>();
        SubagentRunRequest? captured = null;
        inner.RunAsync(Arg.Do<SubagentRunRequest>(r => captured = r), Arg.Any<CancellationToken>())
             .Returns(ZeroAlloc.Results.Result<AgentTurnResult, AgentError>.Success(TurnWith("ok")));

        await Runner(inner).RunAsync(RequestWithoutBudget() with { AgentId = agent }, default);

        captured.Should().NotBeNull("the decorator must delegate to the inner runner");
        return captured!.Budget;
    }

    private static BudgetedSubagentRunner Runner(ISubagentRunner inner)
    {
        // DetachedRuns carries the scout's ceiling, deliberately different from every turn budget below, so a runner that
        // stamped it would be caught.
        var options = Options.Create(new DetachedRunOptions
        {
            PrincipalId = "workflow-test",
            Roles = ["workflow"],
            MaxTotalTokens = 150_000,
            DeadlineSeconds = 300,
        });

        return new BudgetedSubagentRunner(inner, options, BoundWorkflow(), Catalog());
    }

    /// <summary>Binds the turn budgets through the real configuration binder, the way the host does.</summary>
    private static WorkflowConfig BoundWorkflow()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Thalos:Workflow:TurnBudgets:Default"] = "875000",
                ["Thalos:Workflow:TurnBudgets:Agents:implementer"] = "875000",
                ["Thalos:Workflow:TurnBudgets:Agents:reviewer"] = "600000",
            })
            .Build();
        var options = new DaedalusAgentsOptions();
        configuration.GetSection(DaedalusAgentsOptions.SectionName).Bind(options);
        return options.Workflow;
    }

    private static IAgentCatalog Catalog()
    {
        var catalog = Substitute.For<IAgentCatalog>();
        Serve(catalog, ImplementerId, "implementer");
        Serve(catalog, ReviewerId, "reviewer");
        Serve(catalog, ArchitectId, "Daedalus Architect");
        Serve(catalog, CapitalisedReviewerId, "Reviewer");
        return catalog;
    }

    private static void Serve(IAgentCatalog catalog, AgentId id, string name) =>
        catalog.TryGet(id, Arg.Any<string?>(), out Arg.Any<AgentDefinition?>()).Returns(call =>
        {
            call[2] = new AgentDefinition { Id = id, Name = name, Instructions = "x" };
            return true;
        });

    private static SubagentRunRequest RequestWithoutBudget() => new()
    {
        AgentId = AnyAgentId,
        Task = "do the thing",
        Caller = new DetachedPrincipal("workflow:test:00000000-0000-0000-0000-000000000000", ["workflow"]),
    };

    private static AgentTurnResult TurnWith(string text) =>
        new(TurnId.New(), new SessionId(Guid.Empty), text, default, [], TimeSpan.Zero);
}
