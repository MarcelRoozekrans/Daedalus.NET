using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ZeroAlloc.Outbox.EfCore;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Migrations;

/// <summary>
///     Runs the real migration chain on a throwaway database: up to the migration before
///     <c>AddChannelMessageOutbox</c>, then to latest, then asserts the <c>OutboxMessages</c> table exists and
///     round-trips a row — and that rolling back past it leaves a chain the predecessors' <c>Down</c> methods can
///     still run.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AddChannelMessageOutboxMigrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Migration_creates_the_outbox_messages_table_and_round_trips_a_row()
    {
        await RunMigrationAsync(async db =>
        {
            var entity = new OutboxMessageEntity
            {
                TypeName = "Daedalus.Agents.Channels.ChannelMessageQueued",
                Payload = "{\"ChannelId\":\"telegram\",\"ConversationId\":\"482910337\"}"u8.ToArray(),
                // Non-zero on purpose: RetryCount's type default is 0, so asserting a freshly-inserted row
                // reads back as 0 would pass even if the column were mapped to the wrong property entirely.
                // Seeding a non-default value and asserting THAT proves the column round-trips for real.
                RetryCount = 4,
            };

            db.OutboxMessages.Add(entity);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var loaded = await db.OutboxMessages.AsNoTracking().SingleAsync();
            loaded.TypeName.Should().Be("Daedalus.Agents.Channels.ChannelMessageQueued");
            loaded.Status.Should().Be(OutboxMessageStatus.Pending);
            loaded.RetryCount.Should().Be(4);
            loaded.DeadLetterError.Should().BeNull();

            var indexExists = await db.Database.SqlQuery<bool>(
                $"""SELECT to_regclass('"IX_OutboxMessages_Status_NextRetryAt"') IS NOT NULL AS "Value" """).SingleAsync();
            indexExists.Should().BeTrue();
        });
    }

    [Fact]
    public async Task Rolling_back_past_this_migration_runs_the_rest_of_the_down_chain()
    {
        await RunMigrationAsync(async db =>
        {
            var target = db.Database.GetMigrations().Single(m => m.EndsWith("_AddChannelConversations", StringComparison.Ordinal));

            await db.Database.MigrateAsync(target);

            var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
            applied.Should().NotContain(m => m.EndsWith("_AddChannelMessageOutbox", StringComparison.Ordinal));

            // And forward again, so the chain is runnable in both directions.
            await db.Database.MigrateAsync();
            (await db.OutboxMessages.CountAsync()).Should().Be(0);
        });
    }

    /// <summary>Creates a throwaway database, migrates to the predecessor of <c>AddChannelMessageOutbox</c>, then to latest, then asserts.</summary>
    private async Task RunMigrationAsync(Func<ApplicationDbContext, Task> assert)
    {
        var dbName = $"migrate_{Guid.NewGuid():N}";
        await ExecuteOnServerAsync($"CREATE DATABASE \"{dbName}\"");
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = dbName }.ConnectionString;
        try
        {
            await using var db = new ApplicationDbContext(PostgresFixture.CreateDbContextOptions(connectionString));
            var migrations = db.Database.GetMigrations().ToList();
            var index = migrations.FindIndex(m => m.EndsWith("_AddChannelMessageOutbox", StringComparison.Ordinal));
            index.Should().BeGreaterThan(0);

            // AddAgentMemories' Down needs the vector extension for its ALTER TABLE; Rag.NET installs it in the
            // fixture database, so install it here too before the chain runs.
            await db.Database.ExecuteSqlRawAsync("CREATE EXTENSION IF NOT EXISTS vector");
            await db.Database.MigrateAsync(migrations[index - 1]);
            await db.Database.MigrateAsync();

            await assert(db);
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
