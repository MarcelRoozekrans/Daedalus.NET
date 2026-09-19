using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Migrations;

/// <summary>
///     Runs the real migration chain on a throwaway database: up to the migration before
///     <c>AddScheduledRunRepository</c>, then to latest, then asserts the <c>Repository</c> column exists on
///     <c>ScheduledRuns</c> with the expected type and nullability and round-trips both a configured and an
///     unconfigured repository — and that rolling back past it leaves a chain the predecessors' <c>Down</c>
///     methods can still run. <c>PostgresFixture</c> builds its own schema with <c>EnsureCreatedAsync</c>, not
///     <c>MigrateAsync</c>, so nothing else in this solution exercises this migration; without this test a broken
///     one would pass the entire suite.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AddScheduledRunRepositoryMigrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Migration_adds_a_nullable_Repository_column_and_round_trips_it()
    {
        await RunMigrationAsync(async db =>
        {
            var dataType = await db.Database.SqlQuery<string>($"""
                SELECT data_type AS "Value" FROM information_schema.columns
                WHERE table_name = 'ScheduledRuns' AND column_name = 'Repository'
                """).SingleAsync();
            dataType.Should().Be("character varying");

            var maxLength = await db.Database.SqlQuery<int>($"""
                SELECT character_maximum_length AS "Value" FROM information_schema.columns
                WHERE table_name = 'ScheduledRuns' AND column_name = 'Repository'
                """).SingleAsync();
            maxLength.Should().Be(ScheduledRun.MaxRepositoryLength);

            var isNullable = await db.Database.SqlQuery<string>($"""
                SELECT is_nullable AS "Value" FROM information_schema.columns
                WHERE table_name = 'ScheduledRuns' AND column_name = 'Repository'
                """).SingleAsync();
            isNullable.Should().Be("YES");

            var configured = ScheduledRun.Create(
                "with-repository", "0 7 * * *", "RepoDigest", "telegram", "482910337",
                "schedule:daedalus", ["reader"], ScheduleOrigin.Config,
                new DateTime(2026, 9, 19, 7, 0, 0, DateTimeKind.Utc), repository: "owner/repo").Value;
            var unconfigured = ScheduledRun.Create(
                "without-repository", "0 7 * * *", "OtherTrigger", "telegram", "482910337",
                "schedule:daedalus", ["reader"], ScheduleOrigin.Config,
                new DateTime(2026, 9, 19, 7, 0, 0, DateTimeKind.Utc)).Value;

            db.ScheduledRuns.AddRange(configured, unconfigured);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            (await db.ScheduledRuns.AsNoTracking().SingleAsync(r => r.Name == "with-repository")).Repository
                .Should().Be("owner/repo");
            (await db.ScheduledRuns.AsNoTracking().SingleAsync(r => r.Name == "without-repository")).Repository
                .Should().BeNull();
        });
    }

    [Fact]
    public async Task Rolling_back_past_this_migration_runs_the_rest_of_the_down_chain()
    {
        await RunMigrationAsync(async db =>
        {
            var target = db.Database.GetMigrations().Single(m => m.EndsWith("_AddFailedAtStep", StringComparison.Ordinal));

            await db.Database.MigrateAsync(target);

            var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
            applied.Should().NotContain(m => m.EndsWith("_AddScheduledRunRepository", StringComparison.Ordinal));

            // And forward again, so the chain is runnable in both directions.
            await db.Database.MigrateAsync();
            (await db.ScheduledRuns.CountAsync()).Should().Be(0);
        });
    }

    /// <summary>Creates a throwaway database, migrates to the predecessor of <c>AddScheduledRunRepository</c>, then to latest, then asserts.</summary>
    private async Task RunMigrationAsync(Func<ApplicationDbContext, Task> assert)
    {
        var dbName = $"migrate_{Guid.NewGuid():N}";
        await ExecuteOnServerAsync($"CREATE DATABASE \"{dbName}\"");
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = dbName }.ConnectionString;
        try
        {
            await using var db = new ApplicationDbContext(PostgresFixture.CreateDbContextOptions(connectionString));
            var migrations = db.Database.GetMigrations().ToList();
            var index = migrations.FindIndex(m => m.EndsWith("_AddScheduledRunRepository", StringComparison.Ordinal));
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
