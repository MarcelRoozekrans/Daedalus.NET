using System.Text.Json;
using Daedalus.Agents;
using Daedalus.Agents.Decisions;
using Daedalus.Agents.Workflow;
using Daedalus.Domain.Entities;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Minos;
using Thalos;
using Thalos.Runtime;
using Thalos.Tools;
using Thalos.Workflow;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Workflow;

/// <summary>
///     Phase 2.10: at most one <c>node-selection</c> record per (run, seq) is the database's rule, a partial unique index
///     on <c>WorkflowRunRecords</c>, and <see cref="NodeSelectionRunner"/> reads a violation of it as "another delivery
///     recorded this visit first", over the real store. The node-usage twin is
///     <see cref="NodeUsageUniqueIndexTests.A_second_append_for_the_same_run_and_seq_is_already_recorded_not_an_error"/>.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class NodeSelectionUniqueIndexTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly AgentId _implementerId = new(new Guid(0x5e3a9c71, 0x2d4b, 0x4e6f, 0x80, 0x91, 0xa2, 0xb3, 0xc4, 0xd5, 0xe6, 0xf7));
    private static readonly string[] _tools = ["workspace__*", "daedalus__*", "roslyn__*", "sandbox__*"];
    private static readonly string[] _resolvedTools = ["workspace__read_file", "daedalus__report_review_outcome", "roslyn__find_callers", "sandbox__build"];
    private static readonly string[] _winnersFilter = ["workspace__*"];
    private static readonly DateTime _at = new(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc);

    public async Task InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    ///     Two deliveries of one visit decide at once. The other one's record lands between this one's read and its
    ///     append, so the append meets the unique index. The runner's append returns <c>Success(false)</c>, it reads the
    ///     winner's record back and the turn runs on that: the model is the winner's <c>cheap</c>, not this decision's
    ///     <c>strong</c>, and the table holds the winner's record alone.
    ///     Red: catch only the exception the EF store threw, <c>DbUpdateException</c>; the violation is a failed write
    ///     and the node fails.
    /// </summary>
    [Fact]
    public async Task Duplicate_node_selection_returns_false()
    {
        var run = RunAt();
        var store = new WorkflowRunRecordStore(fixture.Db);
        var winner = WorkflowRunRecord.Create(
            run.Id, run.CurrentSeq, run.CurrentNode, WorkflowRunRecord.NodeSelectionKind, "earlier", null,
            JsonSerializer.Serialize(new
            {
                level = "Routine",
                model = "cheap",
                tool_filter = _winnersFilter,
                kept_families = 1,
                fallback = (string?)null,
                confidence = 0.8,
            }),
            _at).Value;
        var inner = new CapturingRunner();
        using var client = FakeDecisionClient.Answering(Demand.Hard, 0.9, 0.1, 0.9, 0.9, 0.9, 0.9, 0.9);

        var result = await Runner(inner, client, new RacingRecords(store, winner))
            .RunAsync(new SubagentRunRequest { AgentId = _implementerId, Task = "t", Caller = new WorkflowCaller(run, grant: null) }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Message : "");
        inner.Requests.Should().ContainSingle().Which.Model.Should().Be("cheap");
        (await store.ListAsync(run.Id, WorkflowRunRecord.NodeSelectionKind, CancellationToken.None))
            .Should().ContainSingle().Which.PrincipalId.Should().Be("earlier");
    }

    private static WorkflowRun RunAt() => new()
    {
        Id = Guid.NewGuid(),
        Process = "manufacture",
        ProcessVersion = 11,
        CurrentNode = "implement",
        CurrentSeq = 7,
        Status = WorkflowStatus.Running,
        StartedBy = new RunPrincipal("u-starter", ["developer"]),
        Visits = new Dictionary<string, int>(StringComparer.Ordinal) { ["implement"] = 1 },
        Variables = new Dictionary<string, object?>(StringComparer.Ordinal) { [ReviewHandoff.WorkIntentKey] = "Skip cancelled tasks" },
        Manifest = new RunManifest
        {
            Nodes = new Dictionary<string, NodePin>(StringComparer.Ordinal)
            {
                ["implement"] = new NodePin("implementer", _implementerId, null, ReviewHandoff.ImplementSkillName, "hash"),
            },
        },
    };

    private static NodeSelectionRunner Runner(ISubagentRunner inner, IDecisionClient client, IWorkflowRunRecordStore records)
    {
        var agent = new AgentConfig { Id = _implementerId.ToString(), Name = "implementer", Chartered = true };
        agent.AllowedModels.Add("cheap");
        agent.AllowedModels.Add("strong");
        foreach (var tool in _tools)
        {
            agent.Tools.Add(tool);
        }

        var options = new DaedalusAgentsOptions();
        options.Agents.Add(agent);

        var definition = new AgentDefinition { Id = _implementerId, Name = "implementer", Instructions = "x" };
        var catalog = Substitute.For<IAgentCatalog>();
        catalog.TryGet(_implementerId, Arg.Any<string?>(), out Arg.Any<AgentDefinition?>()).Returns(call =>
        {
            call[2] = definition;
            return true;
        });
        var provider = Substitute.For<IChatClientProvider>();
        provider.DefaultModel.Returns("provider-default");
        IReadOnlyList<AITool> resolved =
        [
            .. _resolvedTools.Select(name => AIFunctionFactory.Create(() => 0, name)),
        ];
        var toolCatalog = Substitute.For<IToolCatalog>();
        toolCatalog.ResolveAsync(Arg.Any<AgentDefinition>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(Result<IReadOnlyList<AITool>, AgentError>.Success(resolved)));
        var factory = Substitute.For<IAgentFactory>();
        factory.ValidateOverridesAsync(
                Arg.Any<AgentDefinition>(), Arg.Any<string?>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(UnitResult<AgentError>.Success()));

        var services = new ServiceCollection();
        services.AddScoped(_ => records);
        return new NodeSelectionRunner(
            inner,
            new NodeSelectionDecider(client, new DecisionsConfig { Timeout = TimeSpan.FromSeconds(5) }, NullLogger<NodeSelectionDecider>.Instance),
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            options,
            catalog,
            provider,
            toolCatalog,
            factory,
            NullLogger<NodeSelectionRunner>.Instance);
    }

    /// <summary>
    ///     The real store, where the other delivery's record is appended right after this delivery's first read: the race
    ///     the unique index decides.
    /// </summary>
    private sealed class RacingRecords(IWorkflowRunRecordStore store, WorkflowRunRecord winner) : IWorkflowRunRecordStore
    {
        private bool _raced;

        public ValueTask AppendAsync(WorkflowRunRecord record, CancellationToken ct) => store.AppendAsync(record, ct);

        public async ValueTask<IReadOnlyList<WorkflowRunRecord>> ListAsync(Guid runId, string? kind, CancellationToken ct)
        {
            var listed = await store.ListAsync(runId, kind, ct);
            if (!_raced)
            {
                _raced = true;
                await store.AppendAsync(winner, ct);
            }

            return listed;
        }
    }

    /// <summary>Captures every request the runner forwards, and answers each with a plain turn.</summary>
    private sealed class CapturingRunner : ISubagentRunner
    {
        public List<SubagentRunRequest> Requests { get; } = [];

        public ValueTask<Result<AgentTurnResult, AgentError>> RunAsync(SubagentRunRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(Result<AgentTurnResult, AgentError>.Success(
                new AgentTurnResult(TurnId.New(), new SessionId(Guid.Empty), "done", default, [], TimeSpan.Zero)));
        }
    }
}
