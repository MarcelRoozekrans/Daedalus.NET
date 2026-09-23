using Daedalus.Agents.Memory;
using Daedalus.Agents.Scheduling;
using Daedalus.Agents.Workflow;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Thalos;
using Thalos.Workflow;
using ZeroAlloc.Authorization;
using ZeroAlloc.Results;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Covers the composition <see cref="WorkflowNodeDispatcherFactory"/> assembles, not just
///     <see cref="BudgetedSubagentRunner"/> in isolation: builds a real <see cref="WorkflowNodeDispatcher"/>
///     through the factory over a mini <see cref="IServiceProvider"/>, dispatches one task node end to end, and
///     asserts the <see cref="ISubagentRunner"/> the factory actually wires receives a stamped budget.
/// </summary>
/// <remarks>
///     <see cref="BudgetedSubagentRunnerTests"/> alone cannot catch the factory forgetting to wrap the runner —
///     it constructs <see cref="BudgetedSubagentRunner"/> directly, so reverting
///     <see cref="WorkflowNodeDispatcherFactory.Create"/> to pass the raw <see cref="ISubagentRunner"/> through
///     leaves every one of those tests green. This test resolves everything through
///     <see cref="WorkflowNodeDispatcherFactory.Create"/> itself, the same call site production DI uses, so it
///     goes red if that call site stops wrapping.
/// </remarks>
public sealed class WorkflowNodeDispatcherFactoryTests
{
    private static readonly AgentId WorkerAgentId = new(new Guid(0x11111111, 0x2222, 0x3333, 0x44, 0x44, 0x55, 0x55, 0x66, 0x66, 0x77, 0x77));

    [Fact]
    public async Task Dispatching_a_task_node_through_the_factory_carries_a_stamped_budget()
    {
        var runId = Guid.NewGuid();
        var run = new WorkflowRun
        {
            Id = runId,
            Process = "smoke-test",
            ProcessVersion = 1,
            CurrentNode = "start",
            CurrentSeq = 1,
            Status = WorkflowStatus.Running,
            Visits = new Dictionary<string, int>(StringComparer.Ordinal) { ["start"] = 1 },
        };

        var definition = new ProcessDefinition
        {
            Name = "smoke-test",
            Version = 1,
            StartNode = "start",
            Nodes = new Dictionary<string, ProcessNode>(StringComparer.Ordinal)
            {
                ["start"] = new() { Agent = "worker", Skill = "do-thing", Next = "finish" },
                ["finish"] = new() { Terminal = "succeeded" },
            },
        };

        var store = Substitute.For<IWorkflowStore>();
        store.FindAsync(runId, Arg.Any<CancellationToken>()).Returns(new ValueTask<WorkflowRun?>(run));

        var definitions = Substitute.For<IProcessDefinitionStore>();
        definitions.GetAsync("smoke-test", 1, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result<ProcessDefinition>>(Result<ProcessDefinition>.Success(definition)));

        var resolver = Substitute.For<IWorkflowReferenceResolver>();
        resolver.ResolveAgentIdAsync("worker", Arg.Any<CancellationToken>())
            .Returns(new ValueTask<AgentId?>(WorkerAgentId));

        var innerRunner = Substitute.For<ISubagentRunner>();
        SubagentRunRequest? captured = null;
        innerRunner.RunAsync(Arg.Do<SubagentRunRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(Result<AgentTurnResult, AgentError>.Success(TurnWith("done")));

        var detachedOptions = Options.Create(new DetachedRunOptions
        {
            PrincipalId = "workflow-test",
            Roles = ["workflow"],
            MaxTotalTokens = 42_000,
            DeadlineSeconds = 180,
        });

        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton(definitions);
        services.AddSingleton(resolver);
        services.AddSingleton<ISubagentRunner>(innerRunner);
        services.AddSingleton(detachedOptions);
        // The two store decorators the factory puts between the dispatcher and IWorkflowStore need these.
        services.AddSingleton(new SquadOptions { Enabled = true, FallbackAgentName = "Daedalus Architect" });
        services.AddSingleton<WorkflowRecallTierLog>();
        await using var provider = services.BuildServiceProvider();

        var dispatcher = WorkflowNodeDispatcherFactory.Create(provider);
        await dispatcher.DispatchAsync(new WorkflowDispatchMessage(runId, run.CurrentSeq, "start"), CancellationToken.None);

        // Falsifiable: reverting WorkflowNodeDispatcherFactory.Create to pass innerRunner straight through
        // (no BudgetedSubagentRunner wrapper) leaves captured.Budget null here.
        captured.Should().NotBeNull("the dispatcher built by the factory must have reached the substituted runner");
        captured!.Budget.Should().NotBeNull();
        captured.Budget.Should().Be(new SubagentBudget(42_000, TimeSpan.FromSeconds(180)));
        captured.Budget.Should().NotBe(SubagentBudget.Default);
    }

    private static AgentTurnResult TurnWith(string text) =>
        new(TurnId.New(), new SessionId(Guid.Empty), text, default, [], TimeSpan.Zero);
}
