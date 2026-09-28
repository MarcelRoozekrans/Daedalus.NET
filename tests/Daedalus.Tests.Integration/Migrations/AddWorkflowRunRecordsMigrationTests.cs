using System.Text.Json;
using Daedalus.Agents.Workflow;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Migrations;

/// <summary>
///     Runs the real migration chain on a throwaway database: up to the migration before
///     <c>AddWorkflowRunRecords</c>, then to latest. It asserts the <c>WorkflowRunRecords</c> columns, key and index
///     as the migration SQL creates them, round-trips a record through <see cref="WorkflowRunRecordStore"/>, and
///     checks that the migration's own <c>Down</c> drops the table. <c>PostgresFixture</c> builds its schema with
///     <c>EnsureCreatedAsync</c>, not <c>MigrateAsync</c>, so nothing else in this solution runs this migration.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AddWorkflowRunRecordsMigrationTests(PostgresFixture fixture)
{
    private const string Migration = "_AddWorkflowRunRecords";

    private static readonly Guid _runId = new(0x3a1f9c7e, 0x52b4, 0x4d0a, 0x9e, 0x61, 0x7c, 0x2b, 0x8d, 0x4e, 0x0f, 0x13);

    [Fact]
    public async Task Migration_creates_the_columns_with_their_types_lengths_and_nullability()
    {
        await RunMigrationAsync(async (_, connectionString) =>
        {
            var columns = await QueryAsync(
                connectionString,
                """
                SELECT column_name, data_type, character_maximum_length, is_nullable
                FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = 'WorkflowRunRecords'
                """,
                r => new ColumnShape(
                    r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetInt32(2), r.GetString(3)));

            columns.Should().BeEquivalentTo(new[]
            {
                new ColumnShape("Id", "bigint", null, "NO"),
                new ColumnShape("RunId", "uuid", null, "NO"),
                new ColumnShape("Seq", "bigint", null, "NO"),
                new ColumnShape("Node", "character varying", 128, "NO"),
                new ColumnShape("Kind", "character varying", 32, "NO"),
                new ColumnShape("PrincipalId", "character varying", 256, "NO"),
                new ColumnShape("StartedById", "character varying", 256, "YES"),
                new ColumnShape("PayloadJson", "jsonb", null, "NO"),
                new ColumnShape("CreatedAt", "timestamp with time zone", null, "NO"),
            });
        });
    }

    [Fact]
    public async Task Migration_keys_the_table_on_an_identity_Id()
    {
        await RunMigrationAsync(async (_, connectionString) =>
        {
            var keyColumns = await QueryAsync(
                connectionString,
                """
                SELECT k.column_name
                FROM information_schema.table_constraints c
                JOIN information_schema.key_column_usage k
                  ON k.constraint_name = c.constraint_name AND k.table_name = c.table_name
                WHERE c.table_name = 'WorkflowRunRecords' AND c.constraint_type = 'PRIMARY KEY'
                ORDER BY k.ordinal_position
                """,
                r => r.GetString(0));
            keyColumns.Should().Equal("Id");

            var identity = await QueryAsync(
                connectionString,
                """
                SELECT is_identity FROM information_schema.columns
                WHERE table_name = 'WorkflowRunRecords' AND column_name = 'Id'
                """,
                r => r.GetString(0));
            identity.Should().Equal("YES");
        });
    }

    [Fact]
    public async Task Migration_indexes_RunId_Kind_and_Seq_in_that_order()
    {
        await RunMigrationAsync(async (_, connectionString) =>
        {
            var indexes = await QueryAsync(
                connectionString,
                """
                SELECT indexdef FROM pg_indexes
                WHERE tablename = 'WorkflowRunRecords' AND indexname = 'IX_WorkflowRunRecords_RunId_Kind_Seq'
                """,
                r => r.GetString(0));

            indexes.Should().ContainSingle().Which.Should().EndWith("""USING btree ("RunId", "Kind", "Seq")""");
        });
    }

    [Fact]
    public async Task A_record_round_trips_through_the_store_on_the_migrated_table()
    {
        await RunMigrationAsync(async (_, connectionString) =>
        {
            var store = new WorkflowRunRecordStore(new ConnectionStringDbContextFactory(connectionString));
            var createdAt = new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);
            var record = WorkflowRunRecord.Create(
                _runId, 5, "implement", WorkflowRunRecord.WorkspaceWriteKind, "workflow:run/implement", "u-admin",
                """{"tool":"workspace__write_file","path":"src/A.cs"}""", createdAt).Value;

            await store.AppendAsync(record, CancellationToken.None);
            var read = (await store.ListAsync(_runId, WorkflowRunRecord.WorkspaceWriteKind, CancellationToken.None))
                .Single();

            read.Seq.Should().Be(5);
            read.Node.Should().Be("implement");
            read.PrincipalId.Should().Be("workflow:run/implement");
            read.StartedById.Should().Be("u-admin");
            read.CreatedAt.Should().Be(createdAt);
            using var payload = JsonDocument.Parse(read.PayloadJson);
            payload.RootElement.GetProperty("path").GetString().Should().Be("src/A.cs");
        });
    }

    [Fact]
    public async Task Rolling_back_to_the_predecessor_migration_drops_the_table()
    {
        await RunMigrationAsync(async (db, connectionString) =>
        {
            var migrations = db.Database.GetMigrations().ToList();
            var predecessor = migrations[migrations.FindIndex(m => m.EndsWith(Migration, StringComparison.Ordinal)) - 1];

            await db.Database.MigrateAsync(predecessor);

            (await TableCountAsync(connectionString)).Should().Be(0);

            // And forward again: Up must run after its own Down. A failure here throws out of MigrateAsync, so
            // there is no assertion after it; the forward shape is what the other tests in this class assert.
            await db.Database.MigrateAsync();
        });
    }

    private static async Task<long> TableCountAsync(string connectionString) =>
        (await QueryAsync(
            connectionString,
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_name = 'WorkflowRunRecords'",
            r => r.GetInt64(0))).Single();

    private static async Task<List<T>> QueryAsync<T>(
        string connectionString, string sql, Func<NpgsqlDataReader, T> read)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<T>();
        while (await reader.ReadAsync())
        {
            rows.Add(read(reader));
        }

        return rows;
    }

    /// <summary>Creates a throwaway database, migrates to the predecessor of this migration, then to latest, then asserts.</summary>
    private async Task RunMigrationAsync(Func<ApplicationDbContext, string, Task> assert)
    {
        var dbName = $"migrate_{Guid.NewGuid():N}";
        await ExecuteOnServerAsync($"CREATE DATABASE \"{dbName}\"");
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = dbName }.ConnectionString;
        try
        {
            await using var db = new ApplicationDbContext(PostgresFixture.CreateDbContextOptions(connectionString));
            var migrations = db.Database.GetMigrations().ToList();
            var index = migrations.FindIndex(m => m.EndsWith(Migration, StringComparison.Ordinal));
            index.Should().BeGreaterThan(0);

            // AddAgentMemories' Down needs the vector extension for its ALTER TABLE; Rag.NET installs it in the
            // fixture database, so install it here too before the chain runs.
            await db.Database.ExecuteSqlRawAsync("CREATE EXTENSION IF NOT EXISTS vector");
            await db.Database.MigrateAsync(migrations[index - 1]);
            await db.Database.MigrateAsync();

            await assert(db, connectionString);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteOnServerAsync($"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE)");
        }
    }

    private async Task ExecuteOnServerAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed record ColumnShape(string Name, string DataType, int? MaxLength, string IsNullable);

    /// <summary>The store disposes every context it creates, so the factory needs no tracking.</summary>
    private sealed class ConnectionStringDbContextFactory(string connectionString) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(PostgresFixture.CreateDbContextOptions(connectionString));
    }
}
