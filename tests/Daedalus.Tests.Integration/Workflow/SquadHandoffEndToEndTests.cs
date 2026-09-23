using System.Data.Async.Adapters;
using System.Text.Json;
using Daedalus.Agents.Memory;
using Daedalus.Agents.Scheduling;
using Daedalus.Agents.Tools;
using Daedalus.Agents.Workflow;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Thalos;
using Thalos.Workflow;
using Thalos.Workflow.Orm;
using ZeroAlloc.ORM.Migrations;
using ZeroAlloc.Outbox.Orm;
using ZeroAlloc.Results;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Workflow;

/// <summary>
///     Drives a two-role manufacturing pipeline — an <c>implement</c> node that reports variables through its
///     outcome tool, and a lens-running <c>review</c> node — through the real <c>OrmWorkflowStore</c>, the real
///     Thalos <see cref="WorkflowNodeDispatcher"/> and the real
///     <see cref="WorkflowNodeDispatcherFactory"/> composition, against a real Postgres database. Only
///     <see cref="ISubagentRunner"/> and the agent-name lookup are substituted; the store, the outbox, the
///     dispatcher, the variable plumbing and both store decorators are the shipped ones.
/// </summary>
/// <remarks>
///     <b>This suite exists because the assertion it makes could not fail before.</b> Task B4 proved "the
///     reviewer does not receive <c>summary</c> or <c>rationale</c>" against Thalos 0.8.0, where
///     <c>WorkflowNodeDispatcher</c> populated no variables at all — the bag was empty on every path, so the
///     claim held for a reason unrelated to the projection meant to enforce it. Thalos 0.9.0 both carries a
///     node's reported variables into <see cref="WorkflowRun.Variables"/> and renders the whole bag into the
///     next node's task text, which is the first time that assertion can fail for a real reason. Every test
///     here therefore asserts against the <em>task text the dispatcher actually built</em>, and
///     <see cref="The_run_record_holds_the_narrative_the_reviewer_was_not_shown"/> pins that the withheld
///     values really were in the run, so a green result cannot mean "nothing travelled".
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class SquadHandoffEndToEndTests(PostgresFixture fixture)
{
    private const string ProcessName = "b5-handoff-test";

    private const string FilesTouched = "src/Daedalus.Infrastructure/Persistence/TaskRepository.cs";

    private const string SummarySentinel = "SUMMARY-MUST-NOT-REACH-THE-REVIEWER";

    private const string RationaleSentinel = "RATIONALE-MUST-NOT-REACH-THE-REVIEWER";

    private const string WorkIntent = "Make ClaimNextAsync skip cancelled tasks";

    // Three distinct synthetic ids. They stand in for the real Thalos:Agents entries, which this suite has no
    // booted host to read - what the assertions need is only that the three are told apart, and that the id the
    // dispatcher used came back from the resolver rather than from the process file's name.
    private static readonly AgentId ImplementerId = new(new Guid(0x11111111, 0x2222, 0x4333, 0x84, 0x44, 0x55, 0x55, 0x66, 0x66, 0x77, 0x77));

    private static readonly AgentId ReviewerId = new(new Guid(0x22222222, 0x3333, 0x4444, 0x85, 0x55, 0x66, 0x66, 0x77, 0x77, 0x88, 0x88));

    private static readonly AgentId ArchitectId = new(new Guid(0x33333333, 0x4444, 0x4555, 0x86, 0x66, 0x77, 0x77, 0x88, 0x88, 0x99, 0x99));

    private const string Yaml = $"""
        process: {ProcessName}
        version: 1
        nodes:
          implement:
            agent: implementer
            outcomes: [changed, blocked]
            branch:
              changed: review
              blocked: stopped
          review:
            agent: reviewer
            outcomes: [approved, rejected]
            branch:
              approved: done
              rejected: implement
            lenses: [correctness, falsifiability, mechanism]
          stopped:
            terminal: failed
          done:
            terminal: succeeded
        """;

    /// <summary>The implement node reports all three keys its skill declares, through the outcome tool.</summary>
    [Fact]
    public async Task The_reviewer_is_given_the_files_the_implementer_touched()
    {
        var result = await RunAsync(squadEnabled: true);

        result.ReviewTasks.Should().NotBeEmpty("the review node must have been dispatched at all");
        foreach (var task in result.ReviewTasks)
        {
            task.Should().Contain(FilesTouched,
                "files_touched is written by implement through the outcome tool's variables argument and is the " +
                "pointer the reviewer is supposed to start from; without it the review contract is decorative");
        }
    }

    /// <summary>
    ///     The assertion this whole suite is for. It is paired with
    ///     <see cref="The_run_record_holds_the_narrative_the_reviewer_was_not_shown"/>, which is what stops it
    ///     passing because nothing was written.
    /// </summary>
    [Fact]
    public async Task The_reviewer_is_never_given_the_implementers_summary_or_rationale()
    {
        var result = await RunAsync(squadEnabled: true);

        result.ReviewTasks.Should().NotBeEmpty();
        foreach (var task in result.ReviewTasks)
        {
            task.Should().NotContain(SummarySentinel,
                "a summary is rationale in compressed form; a reviewer handed it reviews the account, not the artifact");
            task.Should().NotContain(RationaleSentinel,
                "the reviewer must judge what is on disk, not the argument made for it");
        }
    }

    /// <summary>
    ///     Makes the assertion above non-vacuous: the withheld values reached the run's own record, so
    ///     "the reviewer did not see them" is a statement about the projection and not about an empty bag.
    /// </summary>
    [Fact]
    public async Task The_run_record_holds_the_narrative_the_reviewer_was_not_shown()
    {
        var result = await RunAsync(squadEnabled: true);

        result.FinalVariables.Should().ContainKey(ReviewHandoff.SummaryKey)
            .WhoseValue.Should().Be(SummarySentinel, "summary stays in the run record for humans and for adjudicate");
        result.FinalVariables.Should().ContainKey(ReviewHandoff.RationaleKey)
            .WhoseValue.Should().Be(RationaleSentinel);
        result.FinalVariables.Should().ContainKey(ReviewHandoff.FilesTouchedKey);
    }

    /// <summary>The implement node still receives everything: only a lens-running node is narrowed.</summary>
    [Fact]
    public async Task A_node_that_runs_no_lenses_is_given_the_whole_bag()
    {
        var result = await RunAsync(squadEnabled: true);

        result.ImplementTasks.Should().NotBeEmpty();
        result.ImplementTasks[0].Should().Contain(WorkIntent,
            "the opening variables reach the first node through Thalos' own rendering of the bag");
    }

    [Fact]
    public async Task With_the_squad_enabled_each_role_is_dispatched_as_itself()
    {
        var result = await RunAsync(squadEnabled: true);

        result.AgentsByNode["implement"].Should().Be(ImplementerId);
        result.AgentsByNode["review"].Should().Be(ReviewerId,
            "otherwise the flag is stuck on the fallback and the squad never runs");
    }

    [Fact]
    public async Task With_the_squad_disabled_every_node_is_dispatched_as_the_fallback_agent()
    {
        var result = await RunAsync(squadEnabled: false);

        result.AgentsByNode["implement"].Should().Be(ArchitectId);
        result.AgentsByNode["review"].Should().Be(ArchitectId,
            "disabling the squad must keep the pipeline runnable on the configuration phase 2.2 proved, " +
            "without editing the process file");
    }

    /// <summary>
    ///     The second, inseparable half of the squad-off behaviour: a <c>Succeeded</c> run in which one agent
    ///     implemented and reviewed its own work must not read like one with independent review.
    /// </summary>
    [Fact]
    public async Task A_squad_off_run_says_so_in_its_own_event_log()
    {
        var result = await RunAsync(squadEnabled: false);

        result.Status.Should().Be(WorkflowStatus.Succeeded, "the fallback must still reach a terminal success");
        result.TransitionModes.Should().NotBeEmpty();
        result.TransitionModes.Should().OnlyContain(m => m != null && m.StartsWith(WorkflowRunModeStore.SquadDisabledPrefix, StringComparison.Ordinal),
            "every transition the run recorded must name the mode that produced it, in workflow_run_event and not only in a log");
        result.TransitionModes[0].Should().Contain("Daedalus Architect",
            "naming the agent is what makes the record readable six months later");
    }

    [Fact]
    public async Task A_squad_on_run_records_the_enabled_mode_instead()
    {
        var result = await RunAsync(squadEnabled: true);

        result.TransitionModes.Should().NotBeEmpty();
        result.TransitionModes.Should().OnlyContain(m => m == WorkflowRunModeStore.SquadEnabledValue,
            "the same channel has to carry both polarities, or a disabled run is distinguishable only by an absence");
    }

    private sealed record RunOutcome(
        WorkflowStatus Status,
        IReadOnlyList<string> ImplementTasks,
        IReadOnlyList<string> ReviewTasks,
        IReadOnlyDictionary<string, AgentId> AgentsByNode,
        IReadOnlyDictionary<string, string> FinalVariables,
        IReadOnlyList<string?> TransitionModes);

    private async Task<RunOutcome> RunAsync(bool squadEnabled)
    {
        var dbName = $"b5_handoff_{Guid.NewGuid():N}";
        await ExecuteOnServerAsync($"CREATE DATABASE \"{dbName}\"");
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = dbName }.ConnectionString;

        try
        {
            await MigrateAsync(connectionString);

            var options = new WorkflowOrmOptions { ConnectionString = connectionString };
            var definitions = new OrmProcessDefinitionStore(options);
            var store = new OrmWorkflowStore(options, definitions);

            var definition = ProcessLoader.Load(Yaml);
            definition.IsSuccess.Should().BeTrue(definition.IsFailure ? definition.Error : null);
            (await definitions.UpsertAndActivateAsync(definition.Value, Yaml, CancellationToken.None)).IsSuccess.Should().BeTrue();

            var runner = new ScriptedRunner();
            var provider = BuildProvider(store, definitions, runner, squadEnabled);
            await using var disposable = provider;

            var nodeDispatcher = WorkflowNodeDispatcherFactory.Create(provider);
            var outboxDispatcher = new WorkflowDispatchOutboxDispatcher(nodeDispatcher);

            await using var dataSource = NpgsqlDataSource.Create(connectionString);
            var poller = new WorkflowOutboxDispatchService(
                dataSource, outboxDispatcher, new WorkflowOutboxDispatchOptions(), NullLogger<WorkflowOutboxDispatchService>.Instance);

            var runId = await store.StartAsync(
                ProcessName, 1, $"b5-handoff:{Guid.NewGuid()}", "implement",
                new Dictionary<string, object?>(StringComparer.Ordinal) { [ReviewHandoff.WorkIntentKey] = WorkIntent },
                CancellationToken.None);

            // implement -> review -> done is three dispatches; a couple of spare ticks so a terminal node that
            // enqueued nothing simply finds an empty batch rather than leaving the run mid-flight.
            for (var tick = 0; tick < 6; tick++)
            {
                await poller.ProcessBatchAsync(CancellationToken.None);
            }

            var run = await store.FindAsync(runId, CancellationToken.None);
            run.Should().NotBeNull();

            return new RunOutcome(
                run!.Status,
                runner.TasksFor("implement"),
                runner.TasksFor("review"),
                runner.AgentsByNode,
                run.Variables.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? "", StringComparer.Ordinal),
                await ReadTransitionModesAsync(connectionString, runId));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteOnServerAsync($"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE)");
        }
    }

    /// <summary>
    ///     Builds the container <see cref="WorkflowNodeDispatcherFactory.Create"/> reads, with the same
    ///     <see cref="SquadWorkflowReferenceResolver"/> wrapping production DI applies. The inner name lookup is
    ///     substituted because a real <c>IAgentCatalog</c> would need a booted host;
    ///     <c>SquadConfigurationDriftTests</c> covers that the real composition wraps it the same way.
    /// </summary>
    private static ServiceProvider BuildProvider(
        IWorkflowStore store, IProcessDefinitionStore definitions, ISubagentRunner runner, bool squadEnabled)
    {
        var names = Substitute.For<IWorkflowReferenceResolver>();
        names.ResolveAgentIdAsync("implementer", Arg.Any<CancellationToken>()).Returns(new ValueTask<AgentId?>(ImplementerId));
        names.ResolveAgentIdAsync("reviewer", Arg.Any<CancellationToken>()).Returns(new ValueTask<AgentId?>(ReviewerId));
        names.ResolveAgentIdAsync("Daedalus Architect", Arg.Any<CancellationToken>()).Returns(new ValueTask<AgentId?>(ArchitectId));

        var squad = new SquadOptions { Enabled = squadEnabled, FallbackAgentName = "Daedalus Architect" };

        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton(definitions);
        services.AddSingleton(squad);
        services.AddSingleton<SquadAgentResolver>();
        services.AddSingleton<WorkflowRecallTierLog>();
        services.AddSingleton<IWorkflowReferenceResolver>(sp => new SquadWorkflowReferenceResolver(names, sp.GetRequiredService<SquadAgentResolver>()));
        services.AddSingleton(runner);
        services.AddSingleton(Options.Create(new DetachedRunOptions
        {
            PrincipalId = "workflow-test",
            Roles = ["workflow"],
            MaxTotalTokens = 42_000,
            DeadlineSeconds = 180,
        }));
        return services.BuildServiceProvider();
    }

    /// <summary>
    ///     Reads <c>squad_mode</c> out of every <c>workflow_run_event</c> row that carries a transition's own
    ///     variables — the run record itself, not a log line, which is what design section 7 asks for.
    /// </summary>
    private static async Task<IReadOnlyList<string?>> ReadTransitionModesAsync(string connectionString, Guid runId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT variables FROM workflow_run_event WHERE run_id = @runId AND variables IS NOT NULL AND kind <> 'Entered' ORDER BY seq", connection);
        command.Parameters.AddWithValue("runId", runId);

        var modes = new List<string?>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            using var document = JsonDocument.Parse(reader.GetString(0));
            modes.Add(document.RootElement.TryGetProperty(WorkflowRunModeStore.SquadModeKey, out var mode) ? mode.GetString() : null);
        }

        return modes;
    }

    /// <summary>
    ///     Answers each node's turn the way the shipped skills instruct: <c>implement</c> reports its three
    ///     declared variables on the outcome call, and each <c>review</c> lens pass reports evidence through
    ///     <c>daedalus__report_review_outcome</c> and the matching verdict through the engine's own tool.
    /// </summary>
    private sealed class ScriptedRunner : ISubagentRunner
    {
        private readonly List<(string Node, string Task)> _turns = [];
        private readonly Dictionary<string, AgentId> _agents = new(StringComparer.Ordinal);
        private int _lensPass;

        public IReadOnlyDictionary<string, AgentId> AgentsByNode => _agents;

        public List<string> TasksFor(string node) =>
            _turns.Where(t => string.Equals(t.Node, node, StringComparison.Ordinal)).Select(t => t.Task).ToList();

        public ValueTask<Result<AgentTurnResult, AgentError>> RunAsync(SubagentRunRequest request, CancellationToken ct = default)
        {
            var run = ((WorkflowCaller)request.Caller).Run;
            _turns.Add((run.CurrentNode, request.Task));
            _agents[run.CurrentNode] = request.AgentId;

            var outcomeToolName = request.RequiredOutcome!.ToolName;
            var turn = string.Equals(run.CurrentNode, "implement", StringComparison.Ordinal)
                ? ImplementTurn(outcomeToolName)
                : ReviewTurn(outcomeToolName, _lensPass++);

            return ValueTask.FromResult(Result<AgentTurnResult, AgentError>.Success(turn));
        }

        private static AgentTurnResult ImplementTurn(string outcomeToolName) => new(
            TurnId.New(), new SessionId(Guid.Empty), "applied one code action", default,
            [
                new ToolCallSummary(
                    ToolCallId.New(),
                    outcomeToolName,
                    JsonSerializer.Serialize(new
                    {
                        outcome = "changed",
                        variables = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            [ReviewHandoff.SummaryKey] = SummarySentinel,
                            [ReviewHandoff.FilesTouchedKey] = FilesTouched,
                            [ReviewHandoff.RationaleKey] = RationaleSentinel,
                        },
                    }),
                    Succeeded: true, "ok", TimeSpan.FromMilliseconds(2)),
            ],
            TimeSpan.FromSeconds(1));

        private static AgentTurnResult ReviewTurn(string outcomeToolName, int pass)
        {
            var lens = pass switch { 0 => "correctness", 1 => "falsifiability", _ => "mechanism" };
            return new AgentTurnResult(
                TurnId.New(), new SessionId(Guid.Empty), $"{lens}: approved", default,
                [
                    new ToolCallSummary(
                        ToolCallId.New(),
                        DaedalusReviewTools.QualifiedReportReviewOutcomeToolName,
                        JsonSerializer.Serialize(new
                        {
                            lens,
                            verdict = "approved",
                            @checked = """["TaskRepository.ClaimNextAsync now filters cancelled rows"]""",
                        }),
                        Succeeded: true, "Recorded", TimeSpan.FromMilliseconds(3)),
                    new ToolCallSummary(
                        ToolCallId.New(),
                        outcomeToolName,
                        $$"""{"{{OutcomeToolSchema.ArgumentName}}":"approved"}""",
                        Succeeded: true, "ok", TimeSpan.FromMilliseconds(1)),
                ],
                TimeSpan.FromSeconds(2));
        }
    }

    private static async Task MigrateAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var asyncConnection = connection.AsAsync();
        var dialect = new PostgresMigrationDialect();

        await new MigrationRunner(asyncConnection, OutboxOrmMigrations.Postgres, dialect).RunAsync();
        await new MigrationRunner(asyncConnection, WorkflowOrmMigrations.Postgres, dialect).RunAsync();
    }

    private async Task ExecuteOnServerAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
