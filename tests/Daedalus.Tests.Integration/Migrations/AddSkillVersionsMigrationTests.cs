using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Migrations;

/// <summary>
///     Runs the real migration chain on a throwaway database: up to the migration before
///     <c>AddSkillVersions</c>, seeds a <c>Skills</c> row there, then to latest, then asserts the backfill gave
///     that row a matching <c>SkillVersions</c> row — and that rolling back past it leaves a chain the
///     predecessors' <c>Down</c> methods can still run. <c>PostgresFixture</c> builds its own schema with
///     <c>EnsureCreatedAsync</c>, not <c>MigrateAsync</c>, so nothing else in this solution exercises this
///     migration's hand-added backfill; without this test a broken one would pass the entire suite.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AddSkillVersionsMigrationTests(PostgresFixture fixture)
{
    /// <summary>
    ///     Red: deleting the <c>INSERT ... SELECT ... FROM "Skills"</c> statement from
    ///     <c>AddSkillVersions.Up</c> leaves <c>SkillVersions</c> empty for a <c>Skills</c> row that existed
    ///     before the migration ran, and this assertion fails.
    /// </summary>
    [Fact]
    public async Task Migration_backfills_a_version_for_every_preexisting_skill()
    {
        await RunMigrationAsync(
            seed: async db =>
            {
                var skill = Skill.Create(
                    "daedalus-migrations", "How to add and apply an EF Core migration in this repo.",
                    "# Adding a migration\n1. ...", ["dotnet", "ef"],
                    "skills/daedalus-migrations/SKILL.md", "abc123", isActive: true,
                    new DateTime(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc)).Value;

                db.Skills.Add(skill);
                await db.SaveChangesAsync();
            },
            assert: async db =>
            {
                db.ChangeTracker.Clear();

                var version = await db.SkillVersions.AsNoTracking().SingleAsync();
                version.Name.Should().Be("daedalus-migrations");
                version.ContentHash.Should().Be("abc123");
                version.Body.Should().Be("# Adding a migration\n1. ...");
                version.Tags.Should().Equal("dotnet", "ef");
                version.CreatedAt.Should().Be(new DateTime(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc));
            });
    }

    [Fact]
    public async Task Migration_creates_the_skill_versions_table_and_round_trips_a_version_of_its_own()
    {
        await RunMigrationAsync(
            seed: _ => Task.CompletedTask,
            assert: async db =>
            {
                var version = SkillVersion.Create(
                    "release", "abc123", "How we cut a release.", "# Releasing\n1. Tag it.\n", ["ops"],
                    "release/SKILL.md", new DateTime(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc)).Value;

                db.SkillVersions.Add(version);
                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();

                var loaded = await db.SkillVersions.AsNoTracking().SingleAsync();
                loaded.Name.Should().Be("release");
                loaded.ContentHash.Should().Be("abc123");
                loaded.Tags.Should().Equal("ops");
            });
    }

    [Fact]
    public async Task Rolling_back_past_this_migration_runs_the_rest_of_the_down_chain()
    {
        await RunMigrationAsync(
            seed: _ => Task.CompletedTask,
            assert: async db =>
            {
                var target = db.Database.GetMigrations().Single(m => m.EndsWith("_AddScheduledRunRepository", StringComparison.Ordinal));

                await db.Database.MigrateAsync(target);

                var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
                applied.Should().NotContain(m => m.EndsWith("_AddSkillVersions", StringComparison.Ordinal));

                // And forward again, so the chain is runnable in both directions.
                await db.Database.MigrateAsync();
                (await db.SkillVersions.CountAsync()).Should().Be(0);
            });
    }

    /// <summary>Creates a throwaway database, migrates to the predecessor of <c>AddSkillVersions</c>, seeds through it, migrates to latest, then asserts.</summary>
    private async Task RunMigrationAsync(Func<ApplicationDbContext, Task> seed, Func<ApplicationDbContext, Task> assert)
    {
        var dbName = $"migrate_{Guid.NewGuid():N}";
        await ExecuteOnServerAsync($"CREATE DATABASE \"{dbName}\"");
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = dbName }.ConnectionString;
        try
        {
            await using var db = new ApplicationDbContext(PostgresFixture.CreateDbContextOptions(connectionString));
            var migrations = db.Database.GetMigrations().ToList();
            var index = migrations.FindIndex(m => m.EndsWith("_AddSkillVersions", StringComparison.Ordinal));
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
