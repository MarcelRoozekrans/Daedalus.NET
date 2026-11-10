using System.Data.Async.Adapters;
using Daedalus.Infrastructure.Persistence.Migrations;
using Daedalus.Tests.Integration.Fixtures;
using Npgsql;
using ZeroAlloc.ORM;
using ZeroAlloc.ORM.Migrations;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Migrations;

/// <summary>
///     Phase 3.5, Task 2: how <see cref="DaedalusMigrations"/> meets each kind of database. An EF database at the
///     frozen head is adopted, a new one is built, and one at any other EF state is refused before anything runs
///     (Review Focus 5).
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class BaselineAdoptionTests(PostgresFixture fixture)
{
    private const string ProjectId = "0b8f6c2e-4a1d-4e6b-9c3f-5d7e8a9b0c1d";

    private const string HistorySql =
        "SELECT version || ' ' || name FROM __zaorm_migrations WHERE source = '" + DaedalusMigrations.SourceName + "'";

    [Fact]
    public async Task Ef_database_records_version_1_without_running_it_and_then_runs_version_2()
    {
        await WithDatabaseAsync(ef: true, async connectionString =>
        {
            await EfChainDatabase.ExecuteAsync(connectionString, SeedSql, CancellationToken.None);
            var before = await CountsAsync(connectionString);
            before.Should().OnlyContain(c => c.EndsWith(" 1", StringComparison.Ordinal), "every table holds one seeded row");
            (await DeadColumnsAsync(connectionString)).Should().HaveCount(6, "an EF database still has the six columns");

            var applied = await RunAsync(connectionString);

            applied.Select(m => $"{m.Version} {m.Name}").Should().Equal(
                ["2 drop_dead_rowversions"],
                "an adopted database records the baseline without running it, and runs migration 2");
            (await ReadAsync(connectionString, HistorySql)).Should().Equal("1 baseline", "2 drop_dead_rowversions");
            (await DeadColumnsAsync(connectionString)).Should().BeEmpty("migration 2 drops the six dead columns");
            (await CountsAsync(connectionString)).Should().Equal(before, "migration 2 deletes no row");
            (await ReadAsync(connectionString, "SELECT \"ProjectName\" FROM \"Projects\" WHERE \"Id\" = '" + ProjectId + "'"))
                .Should().Equal("kept");
        });
    }

    [Fact]
    public async Task Fresh_database_runs_versions_1_and_2()
    {
        await WithDatabaseAsync(ef: false, async connectionString =>
        {
            var applied = await RunAsync(connectionString);

            applied.Select(m => $"{m.Version} {m.Name}").Should().Equal("1 baseline", "2 drop_dead_rowversions");
            (await ReadAsync(connectionString, HistorySql)).Should().Equal("1 baseline", "2 drop_dead_rowversions");
            (await ReadAsync(connectionString, "SELECT to_regclass('\"Projects\"')::text")).Should().Equal("\"Projects\"");
            (await DeadColumnsAsync(connectionString)).Should().BeEmpty("a new database never has the dead columns");
            (await CountsAsync(connectionString)).Should().OnlyContain(c => c.EndsWith(" 0", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task Second_run_applies_nothing()
    {
        await WithDatabaseAsync(ef: true, async connectionString =>
        {
            await RunAsync(connectionString);

            (await RunAsync(connectionString)).Should().BeEmpty();
            (await ReadAsync(connectionString, HistorySql)).Should().Equal("1 baseline", "2 drop_dead_rowversions");
        });
    }

    [Fact]
    public async Task Ef_database_at_an_older_head_is_refused()
    {
        await WithDatabaseAsync(ef: true, async connectionString =>
        {
            await EfChainDatabase.UndoHeadMigrationAsync(connectionString, CancellationToken.None);

            var run = async () => await RunAsync(connectionString);

            (await run.Should().ThrowAsync<ZeroAllocOrmMigrationConflictException>())
                .WithMessage("*'20261012210937_AddTaskWorkflowRunIdIndex'*'" + DaedalusMigrations.EfHeadMigrationId + "'*");
            (await ReadAsync(connectionString, HistorySql)).Should().BeEmpty("a refused database records nothing");
        });
    }

    [Fact]
    public async Task Empty_ef_history_is_refused()
    {
        await WithDatabaseAsync(ef: true, async connectionString =>
        {
            await EfChainDatabase.ExecuteAsync(connectionString, "DELETE FROM \"__EFMigrationsHistory\"", CancellationToken.None);

            var run = async () => await RunAsync(connectionString);

            (await run.Should().ThrowAsync<ZeroAllocOrmMigrationConflictException>()).WithMessage("*no rows*");
            (await ReadAsync(connectionString, HistorySql)).Should().BeEmpty("a refused database records nothing");
        });
    }

    /// <summary>The six tables whose <c>bytea</c> <c>"RowVersion"</c> migration 2 drops (#290), one row each below.</summary>
    private static readonly string[] _deadTables =
    [
        "AgentSessions", "BrainstormSessions", "CodeAnalysisRequests", "ExecutionSessions", "Projects", "RepositoryConfigurations",
    ];

    // Only the NOT NULL columns; the dead RowVersion stays NULL, as EF left it.
    private const string SeedSql = """
        INSERT INTO "AgentSessions" ("Id", "AgentId", "OwnerId", "State", "CreatedAt", "LastActivityAt", "TurnCount",
            "TotalInputTokens", "TotalOutputTokens")
        VALUES (gen_random_uuid(), gen_random_uuid(), 'owner', 0, now(), now(), 0, 0, 0);
        INSERT INTO "BrainstormSessions" ("Id", "ProjectId", "Phase", "CreatedAt")
        VALUES (gen_random_uuid(), gen_random_uuid(), 0, now());
        INSERT INTO "CodeAnalysisRequests" ("Id", "Title", "Description", "RepositoryUrl", "Platform", "FilePath", "Type",
            "Priority", "Requirements", "Status", "CurrentIteration", "MaxIterations", "HasFailedValidation", "CreatedAt")
        VALUES (gen_random_uuid(), 't', 'd', 'u', 0, 'f', 0, 0, '[]', 0, 0, 1, false, now());
        INSERT INTO "ExecutionSessions" ("Id", "WorkerName", "StartedAt", "LastHeartbeat", "IsActive", "TasksCompleted")
        VALUES (gen_random_uuid(), 'w', now(), now(), true, 0);
        INSERT INTO "Projects" ("Id", "Version", "ProjectName", "Description", "CreatedAt")
        VALUES ('0b8f6c2e-4a1d-4e6b-9c3f-5d7e8a9b0c1d', '1', 'kept', 'from before the switch', now());
        INSERT INTO "RepositoryConfigurations" ("Id", "Name", "Url", "Platform", "DefaultBranch", "IsActive", "CreatedAt")
        VALUES (gen_random_uuid(), 'n', 'u', 'p', 'main', true, now());
        """;

    private static Task<IReadOnlyList<string>> DeadColumnsAsync(string connectionString) =>
        ReadAsync(
            connectionString,
            "SELECT table_name FROM information_schema.columns WHERE table_schema = 'public' AND column_name = 'RowVersion' "
            + "AND table_name IN (" + string.Join(", ", _deadTables.Select(t => $"'{t}'")) + ") ORDER BY table_name");

    private static Task<IReadOnlyList<string>> CountsAsync(string connectionString) =>
        ReadAsync(
            connectionString,
            string.Join(
                " UNION ALL ",
                _deadTables.Select(t => $"SELECT '{t} ' || count(*) FROM \"{t}\"")) + " ORDER BY 1");

    private static async Task<IReadOnlyList<Migration>> RunAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return await DaedalusMigrations.CreateRunner(connection.AsAsync()).RunAsync(CancellationToken.None);
    }

    private static Task<IReadOnlyList<string>> ReadAsync(string connectionString, string sql) =>
        EfChainDatabase.ReadStringsAsync(connectionString, sql, CancellationToken.None);

    private async Task WithDatabaseAsync(bool ef, Func<string, Task> test)
    {
        var connectionString = ef
            ? await EfChainDatabase.CreateAsync(fixture.ConnectionString, CancellationToken.None)
            : await EfChainDatabase.CreateEmptyAsync(fixture.ConnectionString, CancellationToken.None);
        try
        {
            await test(connectionString);
        }
        finally
        {
            await EfChainDatabase.DropAsync(fixture.ConnectionString, connectionString);
        }
    }
}
