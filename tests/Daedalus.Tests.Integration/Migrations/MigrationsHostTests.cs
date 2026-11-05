using System.Diagnostics;
using Daedalus.Infrastructure.Persistence.Migrations;
using Daedalus.Tests.Integration.Fixtures;
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
