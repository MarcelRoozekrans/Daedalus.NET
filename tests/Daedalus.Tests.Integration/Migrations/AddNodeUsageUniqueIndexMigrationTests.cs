using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Migrations;

/// <summary>
///     Runs the real chain on a throwaway database, to the migration before <c>AddNodeUsageUniqueIndex</c> and then to
///     latest. The other node-usage tests build their schema from the model, so this is what pins the migration itself.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AddNodeUsageUniqueIndexMigrationTests(PostgresFixture fixture)
{
    private const string Migration = "_AddNodeUsageUniqueIndex";

    private static string InsertSql(string kind) =>
        "INSERT INTO \"WorkflowRunRecords\" (\"RunId\", \"Seq\", \"Node\", \"Kind\", \"PrincipalId\", \"PayloadJson\", \"CreatedAt\") " +
        $"VALUES ('7a1c3e55-2b4d-4c6e-9f01-3d5a7b9c1e22', 4, 'implement', '{kind}', 'p', '{{}}', now())";

    /// <summary>
    ///     The migrated database refuses a second node-usage row for one (run, seq), by the named index, and still lets
    ///     another kind share the seq. Red: comment out <c>CreateIndex</c> in the migration; the second insert succeeds.
    ///     Red: drop its <c>filter</c>; the workspace-write insert is refused.
    /// </summary>
    [Fact]
    public async Task The_migrated_database_refuses_a_second_node_usage_row_for_a_run_and_seq()
    {
        await RunMigrationAsync(async connectionString =>
        {
            await ExecuteAsync(connectionString, InsertSql(WorkflowRunRecord.NodeUsageKind));
            await ExecuteAsync(connectionString, InsertSql(WorkflowRunRecord.WorkspaceWriteKind));
            await ExecuteAsync(connectionString, InsertSql(WorkflowRunRecord.WorkspaceWriteKind));

            var second = async () => await ExecuteAsync(connectionString, InsertSql(WorkflowRunRecord.NodeUsageKind));

            var violation = (await second.Should().ThrowAsync<PostgresException>()).Which;
            violation.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
            violation.ConstraintName.Should().Be(WorkflowRunRecord.NodeUsageIndexName);
        });
    }

    /// <summary>
    ///     A database that already holds duplicate node-usage rows migrates, and keeps the first of each pair. Other kinds
    ///     are left alone. Red: remove the DELETE from <c>Up</c>; the index creation fails on the duplicates.
    ///     Red: delete with <c>d."Id" &lt; k."Id"</c>; the survivor is the later row, and the Id assertion fails.
    /// </summary>
    [Fact]
    public async Task Duplicate_node_usage_rows_are_reduced_to_the_first_before_the_index_is_built()
    {
        long firstId = 0;
        await RunMigrationAsync(
            async connectionString =>
            {
                (await CountAsync(connectionString, WorkflowRunRecord.NodeUsageKind)).Should().Be(1);
                (await CountAsync(connectionString, WorkflowRunRecord.WorkspaceWriteKind)).Should().Be(2, "other kinds keep their duplicates");
                (await QueryAsync(connectionString, "SELECT \"Id\" FROM \"WorkflowRunRecords\" WHERE \"Kind\" = 'node-usage'", r => r.GetInt64(0)))
                    .Should().Equal(firstId);
            },
            async connectionString =>
            {
                await ExecuteAsync(connectionString, InsertSql(WorkflowRunRecord.NodeUsageKind));
                await ExecuteAsync(connectionString, InsertSql(WorkflowRunRecord.NodeUsageKind));
                await ExecuteAsync(connectionString, InsertSql(WorkflowRunRecord.WorkspaceWriteKind));
                await ExecuteAsync(connectionString, InsertSql(WorkflowRunRecord.WorkspaceWriteKind));
                firstId = (await QueryAsync(connectionString, "SELECT MIN(\"Id\") FROM \"WorkflowRunRecords\" WHERE \"Kind\" = 'node-usage'", r => r.GetInt64(0))).Single();
            });
    }

    private static async Task<long> CountAsync(string connectionString, string kind) =>
        (await QueryAsync(connectionString, $"SELECT COUNT(*) FROM \"WorkflowRunRecords\" WHERE \"Kind\" = '{kind}'", r => r.GetInt64(0))).Single();

    private static async Task<List<T>> QueryAsync<T>(string connectionString, string sql, Func<NpgsqlDataReader, T> read)
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

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task RunMigrationAsync(Func<string, Task> assert, Func<string, Task>? seedBefore = null)
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

            await db.Database.ExecuteSqlRawAsync("CREATE EXTENSION IF NOT EXISTS vector");
            await db.Database.MigrateAsync(migrations[index - 1]);
            if (seedBefore is not null)
            {
                await seedBefore(connectionString);
            }

            await db.Database.MigrateAsync();

            await assert(connectionString);
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
}
