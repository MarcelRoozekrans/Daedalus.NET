using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Migrations;

/// <summary>
///     Runs the real migration chain on a throwaway database: up to the migration before
///     <c>AddRoleCharters</c>, then to latest, and asserts the new <c>RoleCharterVersions</c>/<c>RoleCharters</c>
///     tables exist and round-trip a row - and that rolling back past it leaves a chain the predecessors' <c>Down</c>
///     methods can still run. <c>PostgresFixture</c> builds its own schema with <c>EnsureCreatedAsync</c>, not
///     <c>MigrateAsync</c>, so nothing else in this solution exercises this migration; without this test a broken
///     one would pass the entire suite. Unlike <c>AddSkillVersionsMigrationTests</c>, there is no pre-existing
///     table to backfill from - phase 2.3's <c>implementer</c>/<c>reviewer</c> agents were fully declared in
///     <c>Thalos:Agents</c> until this migration's companion change, so both tables start empty.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AddRoleChartersMigrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Migration_creates_both_tables_and_round_trips_a_version_and_its_head()
    {
        await RunMigrationAsync(
            seed: _ => Task.CompletedTask,
            assert: async db =>
            {
                var version = RoleCharterVersion.Create(
                    "reviewer", "abc123", "Reviews a manufacturing pipeline task node's work.",
                    "You review a task node's work.", "claude-opus-5", ["manufacture-review"],
                    "roles/reviewer.md", new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc)).Value;
                db.RoleCharterVersions.Add(version);

                var head = RoleCharterHead.Create("reviewer", "abc123", new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc));
                db.RoleCharters.Add(head);

                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();

                var loadedVersion = await db.RoleCharterVersions.AsNoTracking().SingleAsync();
                loadedVersion.Role.Should().Be("reviewer");
                loadedVersion.ContentHash.Should().Be("abc123");
                loadedVersion.Model.Should().Be("claude-opus-5");
                loadedVersion.Skills.Should().Equal("manufacture-review");

                var loadedHead = await db.RoleCharters.AsNoTracking().SingleAsync();
                loadedHead.Role.Should().Be("reviewer");
                loadedHead.CurrentHash.Should().Be("abc123");
                loadedHead.IsActive.Should().BeTrue();
            });
    }

    [Fact]
    public async Task Rolling_back_past_this_migration_runs_the_rest_of_the_down_chain()
    {
        await RunMigrationAsync(
            seed: _ => Task.CompletedTask,
            assert: async db =>
            {
                var target = db.Database.GetMigrations().Single(m => m.EndsWith("_AddSkillVersions", StringComparison.Ordinal));

                await db.Database.MigrateAsync(target);

                var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
                applied.Should().NotContain(m => m.EndsWith("_AddRoleCharters", StringComparison.Ordinal));

                // And forward again, so the chain is runnable in both directions.
                await db.Database.MigrateAsync();
                (await db.RoleCharterVersions.CountAsync()).Should().Be(0);
                (await db.RoleCharters.CountAsync()).Should().Be(0);
            });
    }

    /// <summary>Creates a throwaway database, migrates to the predecessor of <c>AddRoleCharters</c>, seeds through it, migrates to latest, then asserts.</summary>
    private async Task RunMigrationAsync(Func<ApplicationDbContext, Task> seed, Func<ApplicationDbContext, Task> assert)
    {
        var dbName = $"migrate_{Guid.NewGuid():N}";
        await ExecuteOnServerAsync($"CREATE DATABASE \"{dbName}\"");
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = dbName }.ConnectionString;
        try
        {
            await using var db = new ApplicationDbContext(PostgresFixture.CreateDbContextOptions(connectionString));
            var migrations = db.Database.GetMigrations().ToList();
            var index = migrations.FindIndex(m => m.EndsWith("_AddRoleCharters", StringComparison.Ordinal));
            index.Should().BeGreaterThan(0);

            // AddAgentMemories' Down needs the vector extension for its ALTER TABLE; Rag.NET installs it in the
            // fixture database, so install it here too before the chain runs.
            await db.Database.ExecuteSqlRawAsync("CREATE EXTENSION IF NOT EXISTS vector");
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
