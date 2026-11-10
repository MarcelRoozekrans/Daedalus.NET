using System.Diagnostics;
using Daedalus.Infrastructure.Persistence.Migrations;
using Daedalus.Migrations;
using Daedalus.Tests.Integration.Fixtures;
using Npgsql;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Migrations;

/// <summary>
///     Phase 3.5, Task 3: runs the built <c>Daedalus.Migrations</c> host, the process a deploy runs, against an EF
///     database. The host applies the Daedalus source and then the workflow engine's sources; a database whose EF head
///     is not <see cref="DaedalusMigrations.EfHeadMigrationId"/> makes it log the reason and exit 1 before anything runs
///     (Review Focus 5).
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class MigrationsHostTests(PostgresFixture fixture)
{
    private static readonly TimeSpan _hostTimeout = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task Migrations_host_upgrades_an_ef_database()
    {
        await WithEfDatabaseAsync(undoHead: false, async connectionString =>
        {
            var (exitCode, output) = await RunHostAsync(connectionString);

            exitCode.Should().Be(0, output);
            (await ReadAsync(connectionString, "SELECT version || ' ' || name FROM __zaorm_migrations WHERE source = '"
                + DaedalusMigrations.SourceName + "'")).Should().Equal("1 baseline", "2 drop_dead_rowversions");
            (await ReadAsync(connectionString, "SELECT DISTINCT source FROM __zaorm_migrations WHERE source <> '"
                + DaedalusMigrations.SourceName + "' ORDER BY source"))
                .Should().Equal(["Thalos.Workflow.Orm.Migrations", "ZeroAlloc.Outbox.Orm"], "both workflow engine sources applied");
            (await ReadAsync(connectionString, "SELECT table_name FROM information_schema.tables "
                + "WHERE table_schema = 'public' AND table_name IN ('workflow_run', 'outboxmessages', 'OutboxMessages') ORDER BY 1"))
                .Should().Equal("OutboxMessages", "outboxmessages", "workflow_run");
        });
    }

    [Fact]
    public async Task Migrations_host_upgrades_an_ef_database_with_a_pre_scoping_history_table()
    {
        await WithEfDatabaseAsync(undoHead: false, async connectionString =>
        {
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await PreScopingMigrationHistoryUpgradeTests.BuildLegacyDatabaseAsync(
                    connection, PreScopingMigrationHistoryUpgradeTests.LegacyRows, withSchema: true);
            }

            var (exitCode, output) = await RunHostAsync(connectionString);

            // Red: call PreScopingMigrationHistory.UpgradeAsync after DaedalusMigrations.CreateRunner in Program.cs, as
            // WorkflowEngineMigrator.ApplyAsync did. The Daedalus runner finds the unscoped table first, assigns
            // every row to its own source and exits 1 with a conflict that names rows no Daedalus migration owns.
            exitCode.Should().Be(0, output);

            // Red: attribute every row to sources[0] in PreScopingMigrationHistory.Attribute.
            (await ReadAsync(connectionString, "SELECT source || ' ' || count(*) FROM __zaorm_migrations "
                + "WHERE version < 1010 AND source <> '" + DaedalusMigrations.SourceName + "' GROUP BY source ORDER BY source"))
                .Should().Equal("Thalos.Workflow.Orm.Migrations 9", "ZeroAlloc.Outbox.Orm 2");
            (await ReadAsync(connectionString, "SELECT version || ' ' || name FROM __zaorm_migrations WHERE source = '"
                + DaedalusMigrations.SourceName + "' ORDER BY version")).Should().Equal("1 baseline", "2 drop_dead_rowversions");
        });
    }

    [Fact]
    public async Task Migrations_host_keeps_live_channel_outbox_rows_of_a_scoped_ef_database()
    {
        await WithEfDatabaseAsync(undoHead: false, async connectionString =>
        {
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await WorkflowEngineMigrator.ApplyAsync(connection);
            }

            await EfChainDatabase.ExecuteAsync(
                connectionString,
                "INSERT INTO \"OutboxMessages\" (\"Id\", \"TypeName\", \"Payload\", \"Status\", \"RetryCount\", \"NextRetryAt\", \"CreatedAt\") "
                + "VALUES (gen_random_uuid(), 'Digest', '\\x01', 0, 0, now(), now()), (gen_random_uuid(), 'Digest', '\\x02', 0, 0, now(), now())",
                CancellationToken.None);

            var (exitCode, output) = await RunHostAsync(connectionString);

            // Red: make the baseline or the drop-dead-rowversions migration recreate the "OutboxMessages" table, or let
            // the history upgrade attribute an already scoped table's rows again.
            exitCode.Should().Be(0, output);
            (await ReadAsync(connectionString, "SELECT \"TypeName\" || ' ' || \"Status\" FROM \"OutboxMessages\""))
                .Should().Equal("Digest 0", "Digest 0");
            (await ReadAsync(connectionString, "SELECT source || ' ' || count(*) FROM __zaorm_migrations GROUP BY source ORDER BY source"))
                .Should().Contain("ZeroAlloc.Outbox.Orm 2");
        });
    }

    [Fact]
    public async Task Migrations_host_refuses_an_older_ef_head()
    {
        await WithEfDatabaseAsync(undoHead: true, async connectionString =>
        {
            var (exitCode, output) = await RunHostAsync(connectionString);

            exitCode.Should().Be(1, output);
            output.Should().Contain("Daedalus migration refused")
                .And.Contain("20261012210937_AddTaskWorkflowRunIdIndex")
                .And.Contain(DaedalusMigrations.EfHeadMigrationId);
            (await ReadAsync(connectionString, "SELECT count(*)::text FROM __zaorm_migrations"))
                .Should().Equal(["0"], "a refused database records nothing, workflow sources included");
        });
    }

    private static Task<IReadOnlyList<string>> ReadAsync(string connectionString, string sql) =>
        EfChainDatabase.ReadStringsAsync(connectionString, sql, CancellationToken.None);

    /// <summary>Runs the built host, which the test project references, as a child process against <paramref name="connectionString"/>.</summary>
    private static async Task<(int ExitCode, string Output)> RunHostAsync(string connectionString)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Daedalus.Migrations.dll"));
        start.Environment["ConnectionStrings__daedalus"] = connectionString;

        using var process = Process.Start(start) ?? throw new InvalidOperationException("The migrations host did not start");
        using var timeout = new CancellationTokenSource(_hostTimeout);
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await stdout + await stderr);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
    }

    private async Task WithEfDatabaseAsync(bool undoHead, Func<string, Task> test)
    {
        var connectionString = await EfChainDatabase.CreateAsync(fixture.ConnectionString, CancellationToken.None);
        try
        {
            if (undoHead)
            {
                await EfChainDatabase.UndoHeadMigrationAsync(connectionString, CancellationToken.None);
            }

            await test(connectionString);
        }
        finally
        {
            await EfChainDatabase.DropAsync(fixture.ConnectionString, connectionString);
        }
    }
}
