using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Migrations;

/// <summary>
///     Runs the real migration chain on a throwaway database: up to the migration before <c>AddSkills</c>, then to
///     latest, then asserts the table exists and round-trips a document — and that rolling back past it leaves a chain
///     the predecessors' <c>Down</c> methods can still run (the lesson AddAgentMemories learned the hard way).
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AddSkillsMigrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Migration_creates_the_skills_table_and_round_trips_a_document()
    {
        await RunMigrationAsync(async db =>
        {
            var skill = Skill.Create(
                "daedalus-migrations", "How to add and apply an EF Core migration in this repo.",
                "# Adding a migration\n1. ...", ["dotnet", "ef"],
                "skills/daedalus-migrations/SKILL.md", "abc123", isActive: true,
                new DateTime(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc)).Value;

            db.Skills.Add(skill);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var loaded = await db.Skills.AsNoTracking().SingleAsync();
            loaded.Id.Should().Be("daedalus-migrations");
            loaded.Body.Should().Be("# Adding a migration\n1. ...");
            loaded.Tags.Should().Equal("dotnet", "ef");
            loaded.IsActive.Should().BeTrue();

            var indexExists = await db.Database.SqlQuery<bool>(
                $"""SELECT to_regclass('"IX_Skill_IsActive"') IS NOT NULL AS "Value" """).SingleAsync();
            indexExists.Should().BeTrue();
        });
    }

    [Fact]
    public async Task Rolling_back_past_this_migration_runs_the_rest_of_the_down_chain()
    {
        await RunMigrationAsync(async db =>
        {
            var target = db.Database.GetMigrations().Single(m => m.EndsWith("_AddAgentSessions", StringComparison.Ordinal));

            await db.Database.MigrateAsync(target);

            var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
            applied.Should().NotContain(m => m.EndsWith("_AddSkills", StringComparison.Ordinal));
            applied.Should().NotContain(m => m.EndsWith("_AddAgentMemories", StringComparison.Ordinal));

            // And forward again, so the chain is runnable in both directions.
            await db.Database.MigrateAsync();
            (await db.Skills.CountAsync()).Should().Be(0);
        });
    }

    /// <summary>Creates a throwaway database, migrates to the predecessor of <c>AddSkills</c>, then to latest, then asserts.</summary>
    private async Task RunMigrationAsync(Func<ApplicationDbContext, Task> assert)
    {
        var dbName = $"migrate_{Guid.NewGuid():N}";
        await ExecuteOnServerAsync($"CREATE DATABASE \"{dbName}\"");
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = dbName }.ConnectionString;
        try
        {
            await using var db = new ApplicationDbContext(PostgresFixture.CreateDbContextOptions(connectionString));
            var migrations = db.Database.GetMigrations().ToList();
            var index = migrations.FindIndex(m => m.EndsWith("_AddSkills", StringComparison.Ordinal));
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
