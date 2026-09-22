using System.Data.Async.Adapters;
using System.Globalization;
using Daedalus.Tests.Integration.Fixtures;
using Npgsql;
using Thalos.Workflow;
using Thalos.Workflow.Orm;
using ZeroAlloc.ORM.Migrations;
using ZeroAlloc.Outbox.Orm;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Migrations;

/// <summary>
///     Runs Thalos.NET.Workflow.Orm's and ZeroAlloc.Outbox.Orm's raw-SQL migrations against a throwaway database,
///     then round-trips a real process definition and a real run through <see cref="OrmProcessDefinitionStore"/>
///     and <see cref="OrmWorkflowStore"/> — the actual production code paths, not hand-verified SQL.
/// </summary>
/// <remarks>
///     <c>PostgresFixture</c> builds its own schema with <c>EnsureCreatedAsync</c>/EF Core migrations, neither of
///     which ever runs <see cref="WorkflowOrmMigrations.Postgres"/> — these tables exist nowhere else in the test
///     suite. Without this test, a broken workflow migration (or a broken <c>Daedalus.Migrations</c> wiring of it)
///     would pass every other test in this solution: exactly the gap
///     <c>AddScheduledRunRepositoryMigrationTests</c> exists to close for the EF Core chain, mirrored here for the
///     ORM one. See <c>src/Daedalus.Migrations/Program.cs</c> for the production code this test's migration step
///     mirrors, and <c>DaedalusAgentsServiceCollectionExtensions.AddDaedalusAgents</c> for why
///     <c>EnsureSchemaOnStartup</c> is off there rather than relying on this same sequence running at host boot.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class WorkflowOrmMigrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Migrations_create_the_expected_tables_and_columns()
    {
        await RunAsync(async (connection, _) =>
        {
            (await ColumnExistsAsync(connection, "workflow_run", "id")).Should().BeTrue();
            (await ColumnExistsAsync(connection, "workflow_run", "status")).Should().BeTrue();
            (await ColumnExistsAsync(connection, "workflow_run_event", "run_id")).Should().BeTrue();
            (await ColumnExistsAsync(connection, "outboxmessages", "typename")).Should().BeTrue();

            // Migration 1004: content_hash is NOT NULL with no default — see WorkflowOrmMigrations' own remarks
            // on why applying it ahead of the code that writes content_hash is a deployment hazard, not this
            // test's concern (this test always runs migration and code together).
            var isNullable = await connection.ExecuteScalarAsync(
                "SELECT is_nullable FROM information_schema.columns WHERE table_name = 'process_definition' AND column_name = 'content_hash'");
            isNullable.Should().Be("NO");
        });
    }

    [Fact]
    public async Task A_synced_process_and_a_started_run_round_trip_through_the_real_stores()
    {
        await RunAsync(async (_, connectionString) =>
        {
            var options = new WorkflowOrmOptions { ConnectionString = connectionString };
            var definitions = new OrmProcessDefinitionStore(options);
            var store = new OrmWorkflowStore(options, definitions);

            const string yaml = """
                process: migration-smoke-test
                version: 1
                nodes:
                  finish:
                    terminal: succeeded
                """;
            var definition = ProcessLoader.Load(yaml).Value;

            var upserted = await definitions.UpsertAndActivateAsync(definition, yaml, CancellationToken.None);
            upserted.IsSuccess.Should().BeTrue(upserted.IsFailure ? upserted.Error : null);

            var activeVersion = await definitions.GetActiveVersionAsync("migration-smoke-test", CancellationToken.None);
            activeVersion.Should().Be(1);

            var runId = await store.StartAsync(
                "migration-smoke-test", activeVersion!.Value, $"migration-test:{Guid.NewGuid()}", "finish", CancellationToken.None);

            var run = await store.FindAsync(runId, CancellationToken.None);
            run.Should().NotBeNull();
            run!.Process.Should().Be("migration-smoke-test");
            run.ProcessVersion.Should().Be(1);
            run.CurrentNode.Should().Be("finish");
            run.Status.Should().Be(WorkflowStatus.Running);
        });
    }

    /// <summary>Creates a throwaway database, applies both migration sources, runs <paramref name="assert"/>, then drops it.</summary>
    private async Task RunAsync(Func<NpgsqlConnection, string, Task> assert)
    {
        var dbName = $"workflow_migrate_{Guid.NewGuid():N}";
        await ExecuteOnServerAsync($"CREATE DATABASE \"{dbName}\"");
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = dbName }.ConnectionString;
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            var asyncConnection = connection.AsAsync();
            var dialect = new PostgresMigrationDialect();

            // Outbox schema first, mirroring WorkflowOrmSchemaInitializer's own ordering (see its remarks):
            // OrmWorkflowStore enqueues into it in the same transaction as the workflow tables it writes.
            await new MigrationRunner(asyncConnection, OutboxOrmMigrations.Postgres, dialect).RunAsync();
            await new MigrationRunner(asyncConnection, WorkflowOrmMigrations.Postgres, dialect).RunAsync();

            await assert(connection, connectionString);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteOnServerAsync($"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE)");
        }
    }

    private static async Task<bool> ColumnExistsAsync(NpgsqlConnection connection, string table, string column)
    {
        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM information_schema.columns WHERE table_name = @table AND column_name = @column", connection);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("column", column);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) == 1;
    }

    private async Task ExecuteOnServerAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

file static class NpgsqlConnectionScalarExtensions
{
    public static async Task<object?> ExecuteScalarAsync(this NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }
}
