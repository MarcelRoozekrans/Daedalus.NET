using System.Text.Json;
using Daedalus.Agents;
using Daedalus.Agents.Decisions;
using Daedalus.Agents.Memory;
using Daedalus.Agents.Scheduling;
using Daedalus.Agents.Tools;
using Daedalus.Agents.Workflow;
using Daedalus.Tests.Unit.Decisions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Minos;
using Thalos;
using Thalos.Skills;
using Thalos.Workflow;
using ZeroAlloc.Authorization;
using ZeroAlloc.Results;
using WorkflowRunRecord = Daedalus.Domain.Entities.WorkflowRunRecord;

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
        services.AddSingleton(Substitute.For<ISkillStore>());
        services.AddSingleton(detachedOptions);
        // The two store decorators the factory puts between the dispatcher and IWorkflowStore need these.
        services.AddSingleton(new SquadOptions { Enabled = true, FallbackAgentName = "Daedalus Architect" });
        services.AddSingleton<WorkflowRecallTierLog>();
        services.AddSingleton(TestNodeUsage.Recorder());
        // The caller resolver reads the write grants from it; none are needed for a budget.
        services.AddSingleton(new WorkflowConfig());
        // ReviewLensRunner timestamps and logs the review evidence it records; this node declares no lenses.
        services.AddSingleton(TimeProvider.System);
        services.AddLogging();
        // NodeSelectionRunner's dependencies; this node is not an implement or review node, so it is not decided.
        services.AddSingleton(new DaedalusAgentsOptions());
        services.AddSingleton(new NodeSelectionDecider(null, new DecisionsConfig(), NullLogger<NodeSelectionDecider>.Instance));
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

    private static readonly AgentId ReviewerId = new(new Guid(0x55555555, 0x6666, 0x7777, 0x88, 0x88, 0x99, 0x99, 0xaa, 0xaa, 0xbb, 0xbb));

    /// <summary>
    ///     Phase 2.10: the factory places NodeSelectionRunner outside ReviewLensRunner, so a review's three lens passes run
    ///     on one decision: one question to Minos, one record, and the same model on every pass. Reds, each applied once:
    ///     remove NodeSelectionRunner from the chain, and every pass runs on no model and nothing is asked; move it inside
    ///     ReviewLensRunner, around BudgetedSubagentRunner, and the store below, which lists nothing back, sees three
    ///     questions and three records.
    /// </summary>
    [Fact]
    public async Task A_reviews_three_lens_passes_share_one_decision()
    {
        var runId = Guid.NewGuid();
        var run = new WorkflowRun
        {
            Id = runId,
            Process = "manufacture",
            ProcessVersion = 1,
            CurrentNode = "review",
            CurrentSeq = 3,
            Status = WorkflowStatus.Running,
            Visits = new Dictionary<string, int>(StringComparer.Ordinal) { ["implement"] = 1, ["review"] = 1 },
            Variables = new Dictionary<string, object?>(StringComparer.Ordinal) { ["work_intent"] = "Fix the thing", ["files_touched"] = "src/A.cs" },
            Manifest = new RunManifest
            {
                Nodes = new Dictionary<string, NodePin>(StringComparer.Ordinal)
                {
                    ["review"] = new NodePin("reviewer", ReviewerId, null, ReviewHandoff.ReviewSkillName, "hash"),
                },
                Documents = new Dictionary<string, string>(StringComparer.Ordinal),
            },
        };

        string[] lenses = ["correctness", "falsifiability", "mechanism"];
        var definition = new ProcessDefinition
        {
            Name = "manufacture",
            Version = 1,
            StartNode = "review",
            Nodes = new Dictionary<string, ProcessNode>(StringComparer.Ordinal)
            {
                ["review"] = new()
                {
                    Agent = "reviewer",
                    Skill = ReviewHandoff.ReviewSkillName,
                    Outcomes = ["approved", "rejected"],
                    Branch = new Dictionary<string, string>(StringComparer.Ordinal) { ["approved"] = "done", ["rejected"] = "done" },
                    Lenses = lenses,
                },
                ["done"] = new() { Terminal = "succeeded" },
            },
        };

        var store = Substitute.For<IWorkflowStore>();
        store.FindAsync(runId, Arg.Any<CancellationToken>()).Returns(new ValueTask<WorkflowRun?>(run));
        var definitions = Substitute.For<IProcessDefinitionStore>();
        definitions.GetAsync("manufacture", 1, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result<ProcessDefinition>>(Result<ProcessDefinition>.Success(definition)));
        var resolver = Substitute.For<IWorkflowReferenceResolver>();
        resolver.ResolveAgentIdAsync("reviewer", Arg.Any<CancellationToken>()).Returns(new ValueTask<AgentId?>(ReviewerId));

        // Each pass approves the lens its task names, through the evidence tool and the engine's outcome tool.
        var innerRunner = Substitute.For<ISubagentRunner>();
        var models = new List<string?>();
        innerRunner.RunAsync(Arg.Any<SubagentRunRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var request = call.Arg<SubagentRunRequest>();
            models.Add(request.Model);
            var lens = lenses.First(l => request.Task.Contains($"the {l} lens", StringComparison.Ordinal));
            return ValueTask.FromResult(Result<AgentTurnResult, AgentError>.Success(Approval(lens, request.RequiredOutcome!)));
        });

        var records = Substitute.For<IWorkflowRunRecordStore>();
        records.ListAsync(Guid.Empty, default, default).ReturnsForAnyArgs(new ValueTask<IReadOnlyList<WorkflowRunRecord>>([]));
        var options = new DaedalusAgentsOptions();
        var reviewer = new AgentConfig { Id = ReviewerId.ToString(), Name = "reviewer", Chartered = true };
        reviewer.AllowedModels.Add("cheap");
        reviewer.AllowedModels.Add("strong");
        reviewer.Tools.Add("workspace__*");
        reviewer.Tools.Add("daedalus__*");
        options.Agents.Add(reviewer);
        using var client = FakeDecisionClient.Answering(Demand.Hard, 0.9, 0.9, 0.9, 0.9, 0.9, 0.9, 0.9);

        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton(definitions);
        services.AddSingleton(resolver);
        services.AddSingleton(innerRunner);
        // The pinned skill version the dispatcher loads before the turn.
        var skills = new InMemorySkillStore(TimeProvider.System);
        await skills.UpsertAsync(
            new SkillDocument
            {
                Name = SkillName.Parse(ReviewHandoff.ReviewSkillName),
                Description = "Review a change.",
                Body = "Review it.",
                SourcePath = "manufacture-review/SKILL.md",
                ContentHash = "hash",
                IsActive = true,
                UpdatedAt = DateTimeOffset.UnixEpoch,
            },
            CancellationToken.None);
        services.AddSingleton<ISkillStore>(skills);
        services.AddSingleton(Options.Create(new DetachedRunOptions { PrincipalId = "workflow-test", Roles = ["workflow"], MaxTotalTokens = 42_000, DeadlineSeconds = 180 }));
        services.AddSingleton(new SquadOptions { Enabled = true, FallbackAgentName = "Daedalus Architect" });
        services.AddSingleton<WorkflowRecallTierLog>();
        services.AddSingleton(TestNodeUsage.Recorder());
        services.AddSingleton(new WorkflowConfig());
        services.AddSingleton(TimeProvider.System);
        services.AddLogging();
        services.AddScoped(_ => records);
        services.AddSingleton(Substitute.For<IAgentCatalog>());
        var chatProvider = Substitute.For<IChatClientProvider>();
        chatProvider.DefaultModel.Returns("provider-default");
        services.AddSingleton(chatProvider);
        services.AddSingleton(options);
        services.AddSingleton(new NodeSelectionDecider(client, new DecisionsConfig(), NullLogger<NodeSelectionDecider>.Instance));
        await using var provider = services.BuildServiceProvider();

        var dispatcher = WorkflowNodeDispatcherFactory.Create(provider);
        await dispatcher.DispatchAsync(new WorkflowDispatchMessage(runId, run.CurrentSeq, "review"), CancellationToken.None);

        models.Should().Equal("strong", "strong", "strong");
        client.Calls.Should().Be(1);
        await records.Received(1).AppendAsync(
            Arg.Is<WorkflowRunRecord>(r => r.Kind == WorkflowRunRecord.NodeSelectionKind), Arg.Any<CancellationToken>());
    }

    private static AgentTurnResult Approval(string lens, OutcomeToolSchema outcome) =>
        new(TurnId.New(), new SessionId(Guid.Empty), $"{lens}: approved", default,
        [
            new ToolCallSummary(
                ToolCallId.New(),
                DaedalusReviewTools.QualifiedReportReviewOutcomeToolName,
                JsonSerializer.Serialize(new { lens, verdict = "approved", @checked = """["A.cs line 3 handles the empty case"]""" }),
                Succeeded: true, "Recorded", TimeSpan.FromMilliseconds(3)),
            new ToolCallSummary(
                ToolCallId.New(), outcome.ToolName, $$"""{"{{OutcomeToolSchema.ArgumentName}}":"approved"}""",
                Succeeded: true, "ok", TimeSpan.FromMilliseconds(1)),
        ], TimeSpan.FromSeconds(1));

    private static AgentTurnResult TurnWith(string text) =>
        new(TurnId.New(), new SessionId(Guid.Empty), text, default, [], TimeSpan.Zero);
}
