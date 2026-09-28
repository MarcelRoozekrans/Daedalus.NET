using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Migrations;

/// <summary>
///     Runs the real migration chain on a throwaway database: up to the migration before <c>AddOutboxLease</c>, then
///     to latest. It asserts the lease columns this migration adds to the EF outbox table, <c>OutboxMessages</c>, and
///     that its own <c>Down</c> removes them again. <c>PostgresFixture</c> builds its schema with
///     <c>EnsureCreatedAsync</c>, not <c>MigrateAsync</c>, so nothing else in this solution runs this migration.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AddOutboxLeaseMigrationTests(PostgresFixture fixture)
{
    private const string Migration = "_AddOutboxLease";

    [Fact]
    public async Task Migration_adds_nullable_LockedBy_and_LockedUntil_lease_columns()
    {
        await RunMigrationAsync(async (_, connectionString) =>
        {
            var columns = await LeaseColumnsAsync(connectionString);

            columns.Should().BeEquivalentTo(new[]
            {
                new ColumnShape("LockedBy", "character varying", 128, "YES"),
                new ColumnShape("LockedUntil", "timestamp with time zone", null, "YES"),
            });
        });
    }

    [Fact]
    public async Task Rolling_back_to_the_predecessor_migration_removes_the_lease_columns()
    {
        await RunMigrationAsync(async (db, connectionString) =>
        {
            var migrations = db.Database.GetMigrations().ToList();
            var predecessor = migrations[migrations.FindIndex(m => m.EndsWith(Migration, StringComparison.Ordinal)) - 1];

            await db.Database.MigrateAsync(predecessor);

            (await LeaseColumnsAsync(connectionString)).Should().BeEmpty();

            // And forward again: Up must run after its own Down. A failure here throws out of MigrateAsync, so
            // there is no assertion after it; the forward shape is what the other test in this class asserts.
            await db.Database.MigrateAsync();
        });
    }

    private static async Task<List<ColumnShape>> LeaseColumnsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT column_name, data_type, character_maximum_length, is_nullable
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'OutboxMessages'
              AND column_name IN ('LockedBy', 'LockedUntil')
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new List<ColumnShape>();
        while (await reader.ReadAsync())
        {
            int? maxLength = await reader.IsDBNullAsync(2) ? null : reader.GetInt32(2);
            columns.Add(new ColumnShape(reader.GetString(0), reader.GetString(1), maxLength, reader.GetString(3)));
        }

        return columns;
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
}
