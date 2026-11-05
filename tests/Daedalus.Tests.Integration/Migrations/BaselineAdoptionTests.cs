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
    public async Task Ef_database_records_version_1_without_running_it()
    {
        await WithDatabaseAsync(ef: true, async connectionString =>
        {
            await EfChainDatabase.ExecuteAsync(
                connectionString,
                "INSERT INTO \"Projects\" (\"Id\", \"Version\", \"ProjectName\", \"Description\", \"CreatedAt\") "
                + $"VALUES ('{ProjectId}', '1', 'kept', 'from before the switch', now())",
                CancellationToken.None);

            var applied = await RunAsync(connectionString);

            applied.Should().BeEmpty("an adopted database records the baseline without running it");
            (await ReadAsync(connectionString, HistorySql)).Should().Equal("1 baseline");
            (await ReadAsync(connectionString, "SELECT \"ProjectName\" FROM \"Projects\" WHERE \"Id\" = '" + ProjectId + "'"))
                .Should().Equal("kept");
        });
    }

    [Fact]
    public async Task Fresh_database_runs_version_1()
    {
        await WithDatabaseAsync(ef: false, async connectionString =>
        {
            var applied = await RunAsync(connectionString);

            applied.Select(m => $"{m.Version} {m.Name}").Should().Equal("1 baseline");
            (await ReadAsync(connectionString, HistorySql)).Should().Equal("1 baseline");
            (await ReadAsync(connectionString, "SELECT to_regclass('\"Projects\"')::text")).Should().Equal("\"Projects\"");
        });
    }

    [Fact]
    public async Task Ef_database_at_an_older_head_is_refused()
    {
        await WithDatabaseAsync(ef: true, async connectionString =>
        {
            // Undo the head migration, AddNodeSelectionUniqueIndex, as `dotnet ef database update` to its predecessor would.
            await EfChainDatabase.ExecuteAsync(
                connectionString,
                "DROP INDEX \"UX_WorkflowRunRecords_NodeSelection_RunId_Seq\"; "
                + "DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '" + DaedalusMigrations.EfHeadMigrationId + "'",
                CancellationToken.None);

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

    private static async Task<IReadOnlyList<Migration>> RunAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return await DaedalusMigrations.CreateRunner(connection.AsAsync()).RunAsync(CancellationToken.None);
    }

    private static async Task<List<string>> ReadAsync(string connectionString, string sql)
    {
        var rows = new List<string>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

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
