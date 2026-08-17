using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Migrations;

/// <summary>
///     Runs the real migration chain on a fresh database (the fixture DB uses EnsureCreated): up to the migration before
///     <c>AddAgentMemories</c>, seed <c>StructuredLearnings</c>, migrate to latest, assert the copy and the drop.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AddAgentMemoriesMigrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Migration_copies_structured_learnings_into_agent_memories_and_drops_the_table()
    {
        var dbName = $"migrate_{Guid.NewGuid():N}";
        await ExecuteOnServerAsync($"CREATE DATABASE \"{dbName}\"");
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = dbName }.ConnectionString;
        try
        {
            await using var db = new ApplicationDbContext(PostgresFixture.CreateDbContextOptions(connectionString));
            var migrations = db.Database.GetMigrations().ToList();
            var index = migrations.FindIndex(m => m.EndsWith("_AddAgentMemories", StringComparison.Ordinal));
            index.Should().BeGreaterThan(0);
            await db.Database.MigrateAsync(migrations[index - 1]);

            var taskId = Guid.NewGuid();
            var t0 = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "StructuredLearnings" ("Id","Category","Pattern","Resolution","SourceTaskId","ProjectId","Severity","HitCount","CreatedAt","LastReferencedAt","Tags")
                VALUES ({Guid.NewGuid()}, 0, 'CS1061 missing member', 'Add using ZLinq', {taskId}, NULL, 2, 3, {t0}, {t0.AddDays(1)}, ARRAY['EF CORE','ZLINQ']),
                       ({Guid.NewGuid()}, 2, 'uses primary constructors', 'uses primary constructors', NULL, NULL, 0, 0, {t0.AddHours(1)}, NULL, ARRAY[]::text[])
                """);

            await db.Database.MigrateAsync();

            var rows = await db.AgentMemories.AsNoTracking().OrderBy(m => m.CreatedAt).ToListAsync();
            rows.Should().HaveCount(2);

            var error = rows[0];
            error.OwnerId.Should().Be("daedalus");
            error.AgentId.Should().BeNull();
            error.Kind.Should().Be("learning");
            error.Text.Should().Be("CS1061 missing member\nAdd using ZLinq");
            error.Tags.Should().Equal("errorpattern", "high", "ef core", "zlinq");
            error.Source.Should().Be($"ralph:task/{taskId}");
            error.Importance.Should().Be(0.8);
            error.CreatedAt.Should().Be(t0);
            error.UpdatedAt.Should().Be(t0);
            error.LastRecalledAt.Should().Be(t0.AddDays(1));
            error.RecallCount.Should().Be(3);
            error.IsArchived.Should().BeFalse();
            error.IndexPending.Should().BeTrue();

            var convention = rows[1];
            convention.Text.Should().Be("uses primary constructors");
            convention.Tags.Should().Equal("codeconvention", "low");
            convention.Source.Should().Be("migration");
            convention.Importance.Should().Be(0.3);

            var tableExists = await db.Database.SqlQuery<bool>($"""SELECT to_regclass('"StructuredLearnings"') IS NOT NULL AS "Value" """).SingleAsync();
            tableExists.Should().BeFalse();
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
