using System.Data.Async.Adapters;
using AwesomeAssertions.Execution;
using Daedalus.Agents.Workflow;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Thalos;
using Thalos.Skills;
using Thalos.Workflow;
using Thalos.Workflow.Orm;
using ZeroAlloc.ORM.Migrations;
using ZeroAlloc.Outbox.Orm;
using ZeroAlloc.Results;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Workflow;

/// <summary>
///     Drives <see cref="WorkflowOutboxDispatchService"/> — the workflow engine's outbox poller — against a real
///     Postgres outbox table, end to end: a real
///     <see cref="OrmWorkflowStore.StartAsync(WorkflowStartRequest,System.Threading.CancellationToken)"/>
///     enqueues a real
///     dispatch row, the poller's own <see cref="WorkflowOutboxDispatchService.ProcessBatchAsync"/> claims and
///     dispatches it through a real <see cref="WorkflowNodeDispatcher"/>, and the run and the outbox table are
///     both asserted afterward. Before this test <see cref="WorkflowOutboxDispatchService"/> had no functional
///     coverage anywhere — <c>WorkflowNodeDispatcherFactoryTests</c> exercises the dispatcher, and
///     <c>WorkflowOrmMigrationTests</c> exercises the schema and the stores, but nothing drove the poller's own
///     claim/dispatch/mark-succeeded loop against a real table.
/// </summary>
/// <remarks>
///     The only substitute is <see cref="ISubagentRunner"/> — nothing about the outbox, the store, or the
///     dispatcher is faked. Two <see cref="WorkflowOutboxDispatchService.ProcessBatchAsync"/> calls are needed to
///     reach the run's terminal status: with <see cref="WorkflowOutboxDispatchOptions.BatchSize"/> fixed at 1,
///     the first call dispatches the task node (which enqueues a second row for the terminal node it transitions
///     to), and the second call dispatches that terminal node itself.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class WorkflowOutboxDispatchEndToEndTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_started_run_is_dispatched_and_advanced_to_completion_through_the_real_poller()
    {
        var dbName = $"workflow_e2e_{Guid.NewGuid():N}";
        await ExecuteOnServerAsync($"CREATE DATABASE \"{dbName}\"");
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = dbName }.ConnectionString;

        try
        {
            await MigrateAsync(connectionString);

            var options = new WorkflowOrmOptions { ConnectionString = connectionString };
            var definitions = new OrmProcessDefinitionStore(options);
            var store = new OrmWorkflowStore(options, definitions);

            const string yaml = """
                process: e2e-smoke-test
                version: 1
                nodes:
                  start:
                    agent: worker
                    skill: do-thing
                    next: finish
                  finish:
                    terminal: succeeded
                """;
            var definition = ProcessLoader.Load(yaml).Value;
            (await definitions.UpsertAndActivateAsync(definition, yaml, CancellationToken.None)).IsSuccess.Should().BeTrue();

            var workerAgentId = new AgentId(Guid.NewGuid());
            var resolver = Substitute.For<IWorkflowReferenceResolver>();
            resolver.ResolveAgentIdAsync("worker", Arg.Any<CancellationToken>())
                .Returns(new ValueTask<AgentId?>(workerAgentId));

            var runner = Substitute.For<ISubagentRunner>();
            runner.RunAsync(Arg.Any<SubagentRunRequest>(), Arg.Any<CancellationToken>())
                .Returns(Result<AgentTurnResult, AgentError>.Success(
                    new AgentTurnResult(TurnId.New(), new SessionId(Guid.Empty), "done", default, [], TimeSpan.Zero)));

            var nodeDispatcher = new WorkflowNodeDispatcher(
                store, runner, resolver, definitions, Substitute.For<ISkillStore>(), run => new WorkflowCaller(run), gates: [], hostActions: []);
            var outboxDispatcher = new WorkflowDispatchOutboxDispatcher(nodeDispatcher);

            await using var dataSource = NpgsqlDataSource.Create(connectionString);
            var pollerOptions = new WorkflowOutboxDispatchOptions();
            var poller = new WorkflowOutboxDispatchService(
                dataSource, outboxDispatcher, pollerOptions, NullLogger<WorkflowOutboxDispatchService>.Instance);

            var started = await store.StartAsync(
                new WorkflowStartRequest
                {
                    Process = "e2e-smoke-test",
                    Version = 1,
                    CorrelationKey = $"e2e-test:{Guid.NewGuid()}",
                    StartNode = "start",
                    StartedBy = TestPrincipals.Starter,
                },
                CancellationToken.None);
            started.IsSuccess.Should().BeTrue(started.IsFailure ? started.Error : null);
            var runId = started.Value;

            // First tick: dispatches "start" (a real agent turn through the substituted ISubagentRunner) and
            // transitions the run to "finish" — which OrmWorkflowStore.CompleteNodeAsync enqueues its own
            // dispatch row for, since that transition leaves the run at Running.
            await poller.ProcessBatchAsync(CancellationToken.None);

            var afterFirstTick = await store.FindAsync(runId, CancellationToken.None);
            afterFirstTick.Should().NotBeNull();
            afterFirstTick!.CurrentNode.Should().Be("finish");
            afterFirstTick.Status.Should().Be(WorkflowStatus.Running, "the terminal node has not been dispatched yet");
            await runner.Received(1).RunAsync(Arg.Any<SubagentRunRequest>(), Arg.Any<CancellationToken>());

            // Second tick: dispatches "finish" — a terminal node, no agent call — completing the run.
            await poller.ProcessBatchAsync(CancellationToken.None);

            var afterSecondTick = await store.FindAsync(runId, CancellationToken.None);
            afterSecondTick.Should().NotBeNull();
            afterSecondTick!.Status.Should().Be(WorkflowStatus.Succeeded);
            // The terminal node made no further agent call: still exactly one.
            await runner.Received(1).RunAsync(Arg.Any<SubagentRunRequest>(), Arg.Any<CancellationToken>());

            // Both outbox rows this run ever produced were dispatched and marked succeeded, not left pending.
            // Counted with raw SQL, not ClaimPendingAsync, which would lease every row it returns as a side effect.
            var succeeded = await CountRowsAsync(dataSource, "status = 1");
            var pending = await CountRowsAsync(dataSource, "status = 0");
            using var scope = new AssertionScope();
            succeeded.Should().Be(2,
                "both dispatch rows for this run should have been claimed, dispatched and marked succeeded");
            pending.Should().Be(0, "no dispatch row may be left pending");
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteOnServerAsync($"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE)");
        }
    }

    private static async Task<long> CountRowsAsync(NpgsqlDataSource dataSource, string where)
    {
        await using var command = dataSource.CreateCommand($"SELECT COUNT(*) FROM outboxmessages WHERE {where}");
        return (long)(await command.ExecuteScalarAsync())!;
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
