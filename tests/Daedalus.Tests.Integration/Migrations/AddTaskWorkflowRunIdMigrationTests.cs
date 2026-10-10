using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Migrations;

/// <summary>
///     Runs the real chain on a throwaway database, to the migration before <c>AddTaskWorkflowRunId</c> and then to latest,
///     and checks the new column and the constraint that keeps derived statuses out of storage.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AddTaskWorkflowRunIdMigrationTests(PostgresFixture fixture)
{
    private const string Migration = "_AddTaskWorkflowRunId";

    /// <summary>Red: map the property as required, or as <c>text</c>; the shape differs.</summary>
    [Fact]
    public async Task The_migration_adds_a_nullable_uuid_column()
    {
        await RunMigrationAsync(async (_, connectionString) =>
        {
            var shape = await QueryAsync(
                connectionString,
                """
                SELECT data_type, is_nullable FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = 'Tasks' AND column_name = 'WorkflowRunId'
                """,
                r => (r.GetString(0), r.GetString(1)));

            shape.Should().Equal(("uuid", "YES"));
        });
    }

    /// <summary>
    ///     A derived status cannot be stored. Red: drop the <c>HasCheckConstraint</c> line and regenerate the migration;
    ///     the update succeeds.
    /// </summary>
    [Fact]
    public async Task Storing_a_derived_status_is_refused()
    {
        await RunMigrationAsync(async (db, connectionString) =>
        {
            var project = IntegrationTestFactory.CreateProject();
            var task = IntegrationTestFactory.CreateTask(projectId: project.Id);
            project.AddTask(task).IsSuccess.Should().BeTrue();
            db.Projects.Add(project);
            await db.SaveChangesAsync();

            var store = async () => await ExecuteAsync(connectionString, $"UPDATE \"Tasks\" SET \"Status\" = 5 WHERE \"Id\" = '{task.Id}'");

            (await store.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        });
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

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
}
