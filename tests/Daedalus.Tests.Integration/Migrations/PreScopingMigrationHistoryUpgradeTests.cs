using Daedalus.Migrations;
using Daedalus.Tests.Integration.Fixtures;
using Npgsql;
using ZeroAlloc.ORM.Migrations;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Migrations;

/// <summary>
///     Runs <c>Daedalus.Migrations</c>' workflow engine step, <see cref="WorkflowEngineMigrator.ApplyAsync" />,
///     against a <c>__zaorm_migrations</c> table as ZeroAlloc.ORM wrote it before 2.2.0 scoped versions by source,
///     holding the rows of both <c>OutboxOrmMigrations.Postgres</c> and <c>WorkflowOrmMigrations.Postgres</c>: the
///     state every database Daedalus.Migrations migrated before the 2.2.0 bump is in. Every other migration test
///     starts from an empty database, which has nothing to upgrade.
/// </summary>
/// <remarks>
///     <see cref="LegacyHistoryTableSql" /> and <see cref="LegacyInsertSql" /> are ZeroAlloc.ORM 2.1.1's
///     <c>PostgresMigrationDialect.CreateHistoryTableSql</c> and <c>InsertAppliedVersionSql</c>, verbatim, and
///     <see cref="BuildLegacyDatabaseAsync" /> writes the rows the way its <c>MigrationRunner</c> did: one
///     transaction per migration, its SQL then its row, the outbox source first. <see cref="LegacyRows" /> is the
///     developer database's history table as it stood before the bump, with its names as recorded there.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class PreScopingMigrationHistoryUpgradeTests(PostgresFixture fixture)
{
    private const string OutboxSource = "ZeroAlloc.Outbox.Orm";
    private const string WorkflowSource = "Thalos.Workflow.Orm.Migrations";

    // ZeroAlloc.ORM v2.1.1, src/ZeroAlloc.ORM/Migrations/PostgresMigrationDialect.cs.
    private const string LegacyHistoryTableSql =
        "CREATE TABLE IF NOT EXISTS __zaorm_migrations (" +
        "version INTEGER PRIMARY KEY, " +
        "name TEXT NOT NULL, " +
        "applied_at TIMESTAMPTZ NOT NULL DEFAULT NOW())";

    private const string LegacyInsertSql =
        "INSERT INTO __zaorm_migrations (version, name, applied_at) VALUES (@version, @name, @applied_at::timestamptz)";

    private static readonly (string Source, int Version, string Name)[] LegacyRows =
    [
        (OutboxSource, 1, "create_outbox_messages"),
        (OutboxSource, 2, "add_outbox_lease"),
        (WorkflowSource, 1001, "create_workflow_tables"),
        (WorkflowSource, 1002, "create_process_definition_table"),
        (WorkflowSource, 1003, "create_workflow_run_stranded_index"),
        (WorkflowSource, 1004, "add_process_definition_content_hash"),
        (WorkflowSource, 1005, "add_workflow_run_manifest"),
        (WorkflowSource, 1006, "add_workflow_run_started_by"),
        (WorkflowSource, 1007, "add_workflow_run_last_resume"),
        (WorkflowSource, 1008, "add_workflow_run_event_usage"),
        (WorkflowSource, 1009, "add_workflow_run_event_actor"),
    ];

    [Fact]
    public async Task A_pre_scoping_database_both_sources_wrote_is_upgraded_and_rerunning_is_a_no_op()
    {
        await WithDatabaseAsync(async connection =>
        {
            await BuildLegacyDatabaseAsync(connection, LegacyRows, withSchema: true);

            // Red: remove the PreScopingMigrationHistory.UpgradeAsync call from WorkflowEngineMigrator.ApplyAsync. The
            // outbox runner upgrades the table itself and refuses it with the boot failure's own
            // ZeroAllocOrmMigrationConflictException: 9 of its 11 rows are not migrations of 'ZeroAlloc.Outbox.Orm'.
            var first = (await FluentActions.Awaiting(() => WorkflowEngineMigrator.ApplyAsync(connection))
                .Should().NotThrowAsync()).Subject;

            // Red: pass only OutboxOrmMigrations.Postgres to UpgradeAsync, so the nine workflow rows match no source.
            first.IsSuccess.Should().BeTrue(first.IsFailure ? first.Error : null);

            // Red: append "UPDATE __zaorm_migrations SET applied_at = NOW()" to UpgradeSql.
            (await ReadScopedHistoryAsync(connection)).Should().Equal(ExpectedScopedHistory(LegacyRows));

            var upgraded = await ReadScopedHistoryAsync(connection);

            // Red: drop the "AND NOT EXISTS ... column_name = 'source'" clause from IsUnscopedAsync, so a scoped
            // table is upgraded again and ADD COLUMN source fails with 42701.
            var rerun = (await FluentActions.Awaiting(() => WorkflowEngineMigrator.ApplyAsync(connection))
                .Should().NotThrowAsync()).Subject;

            // Red: return Result<int>.Success(1) instead of 0 on UpgradeAsync's already-scoped path.
            rerun.Value.UpgradedHistoryRows.Should().Be(0);

            // Red: run "UPDATE __zaorm_migrations SET applied_at = NOW()" on UpgradeAsync's already-scoped path.
            (await ReadScopedHistoryAsync(connection)).Should().Equal(upgraded);
        });
    }

    [Fact]
    public async Task No_migration_is_reapplied_after_the_upgrade()
    {
        await WithDatabaseAsync(async connection =>
        {
            // History rows without the schema they recorded: a migration applied again then succeeds and shows up
            // in Applied and in the history, instead of failing on a table that already exists.
            await BuildLegacyDatabaseAsync(connection, LegacyRows, withSchema: false);

            var outcome = await WorkflowEngineMigrator.ApplyAsync(connection);

            // Red: pass only OutboxOrmMigrations.Postgres to UpgradeAsync, so the nine workflow rows match no source.
            outcome.IsSuccess.Should().BeTrue(outcome.IsFailure ? outcome.Error : null);

            // Red: in PreScopingMigrationHistory.Attribute, assign every row to sources[0].Name, which is what the
            // runner's own upgrade does. The workflow source then finds no rows of its own and applies all nine again.
            var legacyVersions = LegacyRows.Select(row => row.Version).ToHashSet();
            var pending = WorkflowEngineMigrator.Sources
                .SelectMany(source => source.GetMigrations())
                .Select(migration => migration.Version)
                .Where(version => !legacyVersions.Contains(version));
            outcome.Value.Applied.Select(migration => migration.Version).Should().BeEquivalentTo(pending);

            // Red: append "UPDATE __zaorm_migrations SET applied_at = NOW()" to UpgradeSql.
            (await ReadScopedHistoryAsync(connection)).Should().Equal(ExpectedScopedHistory(LegacyRows));
        });
    }

    [Fact]
    public async Task A_row_no_source_wrote_refuses_the_upgrade_and_leaves_the_table_unchanged()
    {
        await WithDatabaseAsync(async connection =>
        {
            (string Source, int Version, string Name)[] rows = [.. LegacyRows, ("SomeOtherLibrary", 500, "create_widgets")];
            await BuildLegacyDatabaseAsync(connection, rows, withSchema: false);
            var before = await ReadLegacyHistoryAsync(connection);

            var outcome = await WorkflowEngineMigrator.ApplyAsync(connection);

            // Red: in PreScopingMigrationHistory.Attribute, assign a row no source owns to sources[0].Name instead of
            // failing, which is the runner's own upgrade without its check.
            outcome.IsFailure.Should().BeTrue();

            // Red: leave the unowned row labels out of Attribute's failure message.
            outcome.Error.Should().Contain("500 'create_widgets'").And.Contain("The history table is unchanged");

            // Red: when Attribute fails, run "ALTER TABLE __zaorm_migrations ADD COLUMN source TEXT" and commit
            // instead of rolling back. The read's own column count check fails on the fourth column.
            (await ReadLegacyHistoryAsync(connection)).Should().Equal(before);
        });
    }

    [Fact]
    public async Task A_fresh_database_has_nothing_to_upgrade()
    {
        await WithDatabaseAsync(async connection =>
        {
            // Red: drop the table's own EXISTS clause from IsUnscopedAsync, so an absent table counts as unscoped,
            // as PostgresMigrationDialect.SelectUnscopedHistorySql reports it, and LOCK TABLE fails with 42P01.
            await FluentActions.Awaiting(() => WorkflowEngineMigrator.ApplyAsync(connection)).Should().NotThrowAsync();
        });
    }

    private static List<(string Source, int Version, string Name, DateTime AppliedAt)> ExpectedScopedHistory(
        IEnumerable<(string Source, int Version, string Name)> rows)
        => [.. rows.Select(row => (row.Source, row.Version, row.Name, AppliedAt(row.Version))).OrderBy(row => row.Source, StringComparer.Ordinal).ThenBy(row => row.Version)];

    // A distinct, fixed applied_at per row, so a row whose applied_at is rewritten shows up.
    private static DateTime AppliedAt(int version) => new DateTime(2026, 9, 22, 21, 0, 0, DateTimeKind.Utc).AddMinutes(version);

    private static async Task BuildLegacyDatabaseAsync(
        NpgsqlConnection connection,
        IReadOnlyList<(string Source, int Version, string Name)> rows,
        bool withSchema)
    {
        await using (var create = new NpgsqlCommand(LegacyHistoryTableSql, connection))
        {
            await create.ExecuteNonQueryAsync();
        }

        var migrations = WorkflowEngineMigrator.Sources
            .SelectMany(source => source.GetMigrations().Select(migration => (source.Name, migration)))
            .ToDictionary(entry => (entry.Name, entry.migration.Version), entry => entry.migration);

        foreach (var row in rows)
        {
            await using var transaction = await connection.BeginTransactionAsync();
            if (withSchema)
            {
                await using var apply = new NpgsqlCommand(migrations[(row.Source, row.Version)].Sql, connection, transaction);
                await apply.ExecuteNonQueryAsync();
            }

            await using (var insert = new NpgsqlCommand(LegacyInsertSql, connection, transaction))
            {
                insert.Parameters.AddWithValue("version", row.Version);
                insert.Parameters.AddWithValue("name", row.Name);
                insert.Parameters.AddWithValue("applied_at", AppliedAt(row.Version).ToString("o", System.Globalization.CultureInfo.InvariantCulture));
                await insert.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }
    }

    private static async Task<List<(string Source, int Version, string Name, DateTime AppliedAt)>> ReadScopedHistoryAsync(NpgsqlConnection connection)
    {
        var rows = new List<(string, int, string, DateTime)>();
        await using var command = new NpgsqlCommand(
            "SELECT source, version, name, applied_at FROM __zaorm_migrations ORDER BY source COLLATE \"C\", version", connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.GetInt32(1), reader.GetString(2), await reader.GetFieldValueAsync<DateTime>(3)));
        }

        return rows;
    }

    private static async Task<List<(int Version, string Name, DateTime AppliedAt)>> ReadLegacyHistoryAsync(NpgsqlConnection connection)
    {
        // SELECT * so a source column added by a partial upgrade changes the row shape and fails the read.
        var rows = new List<(int, string, DateTime)>();
        await using var command = new NpgsqlCommand("SELECT * FROM __zaorm_migrations ORDER BY version", connection);
        await using var reader = await command.ExecuteReaderAsync();
        reader.FieldCount.Should().Be(3, "a pre-scoping history table has exactly version, name and applied_at");
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetInt32(0), reader.GetString(1), await reader.GetFieldValueAsync<DateTime>(2)));
        }

        return rows;
    }

    private async Task WithDatabaseAsync(Func<NpgsqlConnection, Task> test)
    {
        var dbName = $"prescoping_{Guid.NewGuid():N}";
        await ExecuteOnServerAsync($"CREATE DATABASE \"{dbName}\"");
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = dbName }.ConnectionString;
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await test(connection);
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
