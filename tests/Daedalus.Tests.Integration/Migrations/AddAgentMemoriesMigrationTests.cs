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
    private static readonly DateTime _t0 = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Migration_copies_structured_learnings_into_agent_memories_and_drops_the_table()
    {
        var taskId = Guid.NewGuid();

        // Free tags exercising every normalisation step: untrimmed, mixed case, a duplicate that only collides after
        // lower-casing, a blank, a whitespace-only entry, one longer than AgentMemory.MaxTagLength, and enough tail
        // entries that the 8-tag cap must be applied *after* the blanks and duplicates are gone.
        var messyTags = new[]
        {
            "  Trimmed  ", "EF Core", "ef core", "", "   ", new string('a', 40),
            "one", "two", "three", "four", "five", "six", "seven",
        };

        await RunMigrationAsync(
            async db => await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "StructuredLearnings" ("Id","Category","Pattern","Resolution","SourceTaskId","ProjectId","Severity","HitCount","CreatedAt","LastReferencedAt","Tags")
                VALUES ({Guid.NewGuid()}, 0, 'CS1061 missing member', 'Add using ZLinq', {taskId}, NULL, 2, 3, {_t0}, {_t0.AddDays(1)}, ARRAY['EF CORE','ZLINQ']),
                       ({Guid.NewGuid()}, 2, 'uses primary constructors', 'uses primary constructors', NULL, NULL, 0, 0, {_t0.AddHours(1)}, NULL, ARRAY[]::text[]),
                       ({Guid.NewGuid()}, 1, 'async streaming', 'stream in batches of 256', NULL, NULL, 1, 0, {_t0.AddHours(2)}, NULL, {messyTags}),
                       ({Guid.NewGuid()}, 3, 'ZLinq 1.5.4', 'zero-allocation LINQ', NULL, NULL, 3, 0, {_t0.AddHours(3)}, NULL, ARRAY[]::text[]),
                       ({Guid.NewGuid()}, 4, 'Railway-oriented programming', 'Result types at the boundary', NULL, NULL, 0, 0, {_t0.AddHours(4)}, NULL, ARRAY[]::text[])
                """),
            async db =>
            {
                var rows = await db.AgentMemories.AsNoTracking().OrderBy(m => m.CreatedAt).ToListAsync();
                rows.Should().HaveCount(5);
                rows.Should().OnlyContain(m => m.OwnerId == "daedalus" && m.Kind == "learning" && m.IndexPending && !m.IsArchived && m.AgentId == null);

                var error = rows[0];
                error.OwnerId.Should().Be("daedalus");
                error.AgentId.Should().BeNull();
                error.Kind.Should().Be("learning");
                error.Text.Should().Be("CS1061 missing member\nAdd using ZLinq");
                error.Tags.Should().Equal("errorpattern", "high", "ef core", "zlinq");
                error.Source.Should().Be($"ralph:task/{taskId}");
                error.Importance.Should().Be(0.8);
                error.CreatedAt.Should().Be(_t0);
                error.UpdatedAt.Should().Be(_t0);
                error.LastRecalledAt.Should().Be(_t0.AddDays(1));
                error.RecallCount.Should().Be(3);
                error.IsArchived.Should().BeFalse();
                error.IndexPending.Should().BeTrue();

                var convention = rows[1];
                convention.Text.Should().Be("uses primary constructors");
                convention.Tags.Should().Equal("codeconvention", "low");
                convention.Source.Should().Be("migration");
                convention.Importance.Should().Be(0.3);
                convention.LastRecalledAt.Should().BeNull();
                convention.RecallCount.Should().Be(0);

                // Trimmed, lower-cased, truncated to 32, blanks dropped, de-duplicated keeping the first occurrence, then
                // the first 8 — exactly what LearningMemoryMapping.Tags does, so a later edit passes AgentMemory.Update.
                var success = rows[2];
                success.Importance.Should().Be(0.5);
                success.Tags.Should().Equal(
                    "successpattern", "medium", "trimmed", "ef core", new string('a', 32), "one", "two", "three", "four", "five");

                var dependency = rows[3];
                dependency.Tags.Should().Equal("dependencyinfo", "critical");
                dependency.Importance.Should().Be(1.0);

                var architecture = rows[4];
                architecture.Tags.Should().Equal("architecturedecision", "low");
                architecture.Text.Should().Be("Railway-oriented programming\nResult types at the boundary");

                // The copy bypasses the aggregate (EF materialises through the backing field), so pin that what it wrote is
                // still valid: the first edit of a migrated memory must not fail validation on its own tags.
                foreach (var row in rows)
                {
                    row.Update(null, row.Tags, null, null, null, DateTime.UtcNow)
                        .IsSuccess.Should().BeTrue($"the copied tags of {row.Id} must survive a later edit");
                }

                await AssertLearningsTableIsGoneAsync(db);
            });
    }

    [Fact]
    public async Task Migration_runs_on_an_empty_learnings_table()
    {
        await RunMigrationAsync(
            _ => Task.CompletedTask,
            async db =>
            {
                (await db.AgentMemories.AsNoTracking().CountAsync()).Should().Be(0);
                await AssertLearningsTableIsGoneAsync(db);
            });
    }

    /// <summary>
    ///     The whole Down chain stays runnable past this migration: <c>AddSemanticEmbeddings.Down</c> drops the
    ///     <c>Embedding</c> column, which the scaffolded <c>CreateTable</c> no longer recreates (the model lost the
    ///     <c>vector(384)</c> mapping), so without the hand-added <c>ALTER TABLE ... ADD COLUMN IF NOT EXISTS</c> a rollback
    ///     fails with <c>42703</c> — after this migration's Down has already dropped every memory.
    /// </summary>
    [Fact]
    public async Task Rolling_back_past_this_migration_runs_the_rest_of_the_down_chain()
    {
        await RunMigrationAsync(
            _ => Task.CompletedTask,
            async db =>
            {
                var target = db.Database.GetMigrations().Single(m => m.EndsWith("_AddTokenUsageColumns", StringComparison.Ordinal));

                await db.Database.MigrateAsync(target);

                var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
                applied.Should().NotContain(m => m.EndsWith("_AddSemanticEmbeddings", StringComparison.Ordinal));
                applied.Should().NotContain(m => m.EndsWith("_AddAgentMemories", StringComparison.Ordinal));
            });
    }

    private static async Task AssertLearningsTableIsGoneAsync(ApplicationDbContext db)
    {
        var tableExists = await db.Database.SqlQuery<bool>($"""SELECT to_regclass('"StructuredLearnings"') IS NOT NULL AS "Value" """).SingleAsync();
        tableExists.Should().BeFalse();
    }

    /// <summary>
    ///     Creates a throwaway database, migrates it to the predecessor of <c>AddAgentMemories</c>, runs <paramref name="seed"/>,
    ///     migrates to latest and runs <paramref name="assert"/>. The database is dropped again either way.
    /// </summary>
    private async Task RunMigrationAsync(Func<ApplicationDbContext, Task> seed, Func<ApplicationDbContext, Task> assert)
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

            await seed(db);

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
