using System.Text.Json;
using Daedalus.Agents.Tools;
using Daedalus.Agents.Workflow;
using Daedalus.Application.Configuration;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Infrastructure.Services;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Thalos;
using Thalos.Workflow;
using ZeroAlloc.Results;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Workflow;

/// <summary>
///     Phase 2.10 final review, item 1, end to end on Postgres: a review whose <c>falsifiability</c> lens is pinned to a
///     dearer model than the node's decision is recorded per model, and cost analytics prices each model's tokens at its own
///     rate. Before the fix the whole review was priced at the first pass's model.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class PinnedLensPricingTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string Decided = "decided-model";
    private const string Pinned = "pinned-model";
    private static readonly string[] Lenses = ["correctness", "falsifiability", "mechanism"];
    private static readonly OutcomeToolSchema ReviewOutcome = new("workflow__report_outcome", ["approved", "rejected"]);

    public async Task InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    ///     Each pass spends 1 000 input and 100 output tokens. The decided model costs 1 and 5 per million, the pinned one 3
    ///     and 15. Priced per model: two passes at the decided rate, 0.003 in all, and one at the pinned rate, 0.0045, so 0.0075.
    ///     Red: drop the lens runner's <c>HoldPasses</c> call, or the recorder's take; all three passes are priced at the
    ///     decided rate, 0.0045, and the pinned model has no slice.
    /// </summary>
    [Fact]
    public async Task A_pinned_lens_is_priced_at_its_own_models_rate()
    {
        IWorkflowRunRecordStore store = new WorkflowRunRecordStore(new FixtureDbContextFactory(fixture));
        var scopes = new ServiceCollection().AddSingleton(store).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var recorder = new NodeUsageRecorder(scopes, TimeProvider.System, NullLogger<NodeUsageRecorder>.Instance);
        var pins = new Dictionary<string, string>(StringComparer.Ordinal) { ["falsifiability"] = Pinned };
        var runner = new ReviewLensRunner(new PassRunner(), Definitions(), scopes, TimeProvider.System, pins, recorder, NullLogger<ReviewLensRunner>.Instance);
        var run = ReviewRun();

        var result = await runner.RunAsync(
            new SubagentRunRequest
            {
                AgentId = AgentId.New(),
                Task = "Load the skill 'manufacture-review' and follow it.",
                Caller = new WorkflowCaller(run, grant: null),
                RequiredOutcome = ReviewOutcome,
                Model = Decided,
            },
            CancellationToken.None);
        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Message : "");
        (await recorder.RecordCompletionAsync(run.Id, run.CurrentSeq, "review", null, result.Value.Usage, CancellationToken.None))
            .Should().BeTrue();

        await using var db = fixture.CreateDbContext();
        var summary = await new CostAnalyticsService(db, Pricing()).GetSummaryAsync();

        var byModel = summary.ByModel.ToDictionary(m => m.Key, StringComparer.Ordinal);
        byModel[Decided].Cost.Should().Be(0.003m);    // 2000 × 1/1M + 200 × 5/1M
        byModel[Pinned].Cost.Should().Be(0.0045m);    // 1000 × 3/1M + 100 × 15/1M
        summary.TotalCost.Should().Be(byModel[Decided].Cost + byModel[Pinned].Cost);
        summary.TotalCost.Should().Be(0.0075m);
        summary.TotalEntries.Should().Be(1, "one review node completed");
    }

    private static IOptions<ModelPricingConfiguration> Pricing() => Options.Create(new ModelPricingConfiguration
    {
        Models = new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase)
        {
            [Decided] = new() { DisplayName = "Decided", InputTokenPricePerMillion = 1.0m, OutputTokenPricePerMillion = 5.0m },
            [Pinned] = new() { DisplayName = "Pinned", InputTokenPricePerMillion = 3.0m, OutputTokenPricePerMillion = 15.0m },
        },
    });

    private static WorkflowRun ReviewRun() => new()
    {
        Id = Guid.NewGuid(),
        Process = "manufacture",
        ProcessVersion = 3,
        CurrentNode = "review",
        CurrentSeq = 4,
        Status = WorkflowStatus.Running,
        Visits = new Dictionary<string, int>(StringComparer.Ordinal) { ["review"] = 1 },
        Variables = new Dictionary<string, object?>(StringComparer.Ordinal) { ["work_intent"] = "Make ClaimNextAsync skip cancelled tasks" },
    };

    private static IProcessDefinitionStore Definitions()
    {
        var definition = new ProcessDefinition
        {
            Name = "manufacture",
            Version = 3,
            StartNode = "review",
            Nodes = new Dictionary<string, ProcessNode>(StringComparer.Ordinal)
            {
                ["review"] = new()
                {
                    Agent = "reviewer",
                    Skill = "manufacture-review",
                    Outcomes = ["approved", "rejected"],
                    Branch = new Dictionary<string, string>(StringComparer.Ordinal) { ["approved"] = "done", ["rejected"] = "done" },
                    Lenses = Lenses,
                },
                ["done"] = new() { Terminal = "succeeded" },
            },
        };
        var definitions = Substitute.For<IProcessDefinitionStore>();
        definitions.GetAsync("manufacture", 3, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result<ProcessDefinition>>(Result<ProcessDefinition>.Success(definition)));
        return definitions;
    }

    /// <summary>Approves each lens in turn, on the model the pass was sent with, spending 1 000 input and 100 output tokens.</summary>
    private sealed class PassRunner : ISubagentRunner
    {
        private int _pass;

        public ValueTask<Result<AgentTurnResult, AgentError>> RunAsync(SubagentRunRequest request, CancellationToken ct = default)
        {
            var lens = Lenses[_pass++];
            var turn = new AgentTurnResult(TurnId.New(), new SessionId(Guid.Empty), $"{lens}: approved", new TurnUsage(1_000, 100, request.Model!),
            [
                new ToolCallSummary(
                    ToolCallId.New(),
                    DaedalusReviewTools.QualifiedReportReviewOutcomeToolName,
                    JsonSerializer.Serialize(new { lens, verdict = "approved", @checked = """["src/A.cs: A.Check is reviewed"]""" }),
                    Succeeded: true, "Recorded", TimeSpan.FromMilliseconds(3)),
                new ToolCallSummary(
                    ToolCallId.New(), ReviewOutcome.ToolName, $$"""{"{{OutcomeToolSchema.ArgumentName}}":"approved"}""",
                    Succeeded: true, "ok", TimeSpan.FromMilliseconds(1)),
            ], TimeSpan.FromSeconds(1));
            return ValueTask.FromResult(Result<AgentTurnResult, AgentError>.Success(turn));
        }
    }

    private sealed class FixtureDbContextFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
    }
}
