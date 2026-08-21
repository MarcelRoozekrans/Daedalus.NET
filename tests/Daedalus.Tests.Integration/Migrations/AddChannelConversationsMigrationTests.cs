using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Migrations;

/// <summary>
///     Runs the real migration chain on a throwaway database: up to the migration before
///     <c>AddChannelConversations</c>, then to latest, then asserts the table exists and round-trips a binding — and
///     that rolling back past it leaves a chain the predecessors' <c>Down</c> methods can still run.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AddChannelConversationsMigrationTests(PostgresFixture fixture)
{
    private static readonly Guid _sessionId = new(0x11111111, 0x2222, 0x3333, 0x44, 0x44, 0x55, 0x55, 0x55, 0x55, 0x55, 0x55);
    private static readonly Guid _agentId = new(0x66666666, 0x7777, 0x8888, 0x99, 0x99, 0xaa, 0xaa, 0xaa, 0xaa, 0xaa, 0xaa);

    [Fact]
    public async Task Migration_creates_the_channel_conversations_table_and_round_trips_a_binding()
    {
        await RunMigrationAsync(async db =>
        {
            var createdAt = new DateTime(2026, 8, 20, 9, 30, 15, DateTimeKind.Utc);
            var conversation = ChannelConversation.Create("telegram", "482910337", _sessionId, _agentId, createdAt).Value;

            db.ChannelConversations.Add(conversation);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var loaded = await db.ChannelConversations.AsNoTracking().SingleAsync();
            loaded.ChannelId.Should().Be("telegram");
            loaded.ConversationId.Should().Be("482910337");
            loaded.SessionId.Should().Be(_sessionId);
            loaded.AgentId.Should().Be(_agentId);
            loaded.CreatedAt.Should().Be(createdAt);
            loaded.LastActivityAt.Should().Be(createdAt);

            var uniqueIndexExists = await db.Database.SqlQuery<bool>(
                $"""SELECT to_regclass('"IX_ChannelConversation_Channel_Conversation"') IS NOT NULL AS "Value" """).SingleAsync();
            uniqueIndexExists.Should().BeTrue();

            // The unique index on (ChannelId, ConversationId) is enforced by the database, not merely by convention:
            // a second row for the same channel + conversation must be rejected at INSERT time.
            var duplicate = ChannelConversation.Create("telegram", "482910337", Guid.NewGuid(), _agentId, createdAt).Value;
            db.ChannelConversations.Add(duplicate);
            var act = async () => await db.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>();
        });
    }

    [Fact]
    public async Task Rolling_back_past_this_migration_runs_the_rest_of_the_down_chain()
    {
        await RunMigrationAsync(async db =>
        {
            var target = db.Database.GetMigrations().Single(m => m.EndsWith("_AddSkills", StringComparison.Ordinal));

            await db.Database.MigrateAsync(target);

            var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
            applied.Should().NotContain(m => m.EndsWith("_AddChannelConversations", StringComparison.Ordinal));

            // And forward again, so the chain is runnable in both directions.
            await db.Database.MigrateAsync();
            (await db.ChannelConversations.CountAsync()).Should().Be(0);
        });
    }

    /// <summary>Creates a throwaway database, migrates to the predecessor of <c>AddChannelConversations</c>, then to latest, then asserts.</summary>
    private async Task RunMigrationAsync(Func<ApplicationDbContext, Task> assert)
    {
        var dbName = $"migrate_{Guid.NewGuid():N}";
        await ExecuteOnServerAsync($"CREATE DATABASE \"{dbName}\"");
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = dbName }.ConnectionString;
        try
        {
            await using var db = new ApplicationDbContext(PostgresFixture.CreateDbContextOptions(connectionString));
            var migrations = db.Database.GetMigrations().ToList();
            var index = migrations.FindIndex(m => m.EndsWith("_AddChannelConversations", StringComparison.Ordinal));
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
