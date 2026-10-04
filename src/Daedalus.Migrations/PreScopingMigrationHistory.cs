using System.Globalization;
using Npgsql;
using ZeroAlloc.ORM.Migrations;
using ZeroAlloc.Results;

namespace Daedalus.Migrations;

/// <summary>
///     Upgrades a <c>__zaorm_migrations</c> history table written before ZeroAlloc.ORM 2.2.0 scoped migration
///     versions by source, when more than one source wrote it, by attributing every row to the source it is a
///     migration of. This is what ZeroAlloc.ORM's migrations cookbook prescribes in "Upgrading a history table from
///     before source scoping", under "When the table holds more than one source's rows": assign the rows yourself,
///     once, in a transaction, before the first run on the new version. The runner's own upgrade assigns every row
///     to the source being run, so it refuses a table that another source wrote rows to, from either source.
/// </summary>
/// <remarks>
///     <para>
///         Before 2.2.0 the table was keyed by <c>version</c> alone, one sequence for the whole database, and
///         <c>Daedalus.Migrations</c> recorded both <c>OutboxOrmMigrations.Postgres</c> (1, 2) and
///         <c>WorkflowOrmMigrations.Postgres</c> (1001 and up) in it. Neither source offset the other's
///         numbers: each recorded its own versions, so a row is attributed by its version and name exactly as
///         the source still discovers them, and kept unchanged apart from its new <c>source</c>. The cookbook's
///         recipe copies the rows into a recreated table because it also maps offset versions back and has to
///         cover SQLite; neither applies here, so this runs the PostgreSQL dialect's own in-place upgrade
///         statements, and the table ends up exactly as the runner's upgrade would leave it.
///     </para>
///     <para>
///         The whole upgrade is one transaction, under the same advisory lock <see cref="MigrationRunner" />
///         takes with <see cref="PostgresMigrationDialect" />'s default key, so it never interleaves with a
///         runner. A table that is absent, as on a fresh database, or already scoped is left alone: rerunning is
///         a no-op. A row that is a migration of no source, or of more than one, makes the upgrade a failure
///         that leaves the table unchanged, because attributing it would either lose it or make a source apply
///         its migration again.
///     </para>
/// </remarks>
public static class PreScopingMigrationHistory
{
    private const string HistoryTable = "__zaorm_migrations";

    private const string LockTableSql = $"LOCK TABLE {HistoryTable} IN ACCESS EXCLUSIVE MODE";

    // PostgresMigrationDialect.UpgradeUnscopedHistorySql, the runner's own in-place upgrade, statement for statement,
    // except that the UPDATE gives each row the source attributed to it rather than every row the source being run.
    // The table ends up exactly as that upgrade leaves it. DROP CONSTRAINT names the key PostgreSQL gave the table
    // ZeroAlloc.ORM created before scoping; a table with another key fails there, and the upgrade rolls back.
    private const string UpgradeSql =
        $"ALTER TABLE {HistoryTable} ADD COLUMN source TEXT; " +
        $"UPDATE {HistoryTable} h SET source = a.source FROM unnest(@versions, @sources) AS a(version, source) " +
        "WHERE h.version = a.version; " +
        $"ALTER TABLE {HistoryTable} ALTER COLUMN source SET NOT NULL; " +
        $"ALTER TABLE {HistoryTable} DROP CONSTRAINT {HistoryTable}_pkey; " +
        $"ALTER TABLE {HistoryTable} ADD PRIMARY KEY (source, version)";

    /// <summary>
    ///     Attributes every row of a pre-scoping history table to the one source in <paramref name="sources" />
    ///     that has a migration with the row's version and name, and moves the table to the scoped layout.
    /// </summary>
    /// <returns>
    ///     The number of rows attributed: zero when the table is absent or already scoped. A failure, with the
    ///     table unchanged, when a row matches no source or several.
    /// </returns>
    public static async Task<Result<int>> UpgradeAsync(
        NpgsqlConnection connection,
        IReadOnlyList<IMigrationSource> sources,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(sources);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", connection, transaction))
        {
            lockCommand.Parameters.AddWithValue("key", PostgresMigrationDialect.DefaultLockKey);
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!await IsUnscopedAsync(connection, transaction, cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return Result<int>.Success(0);
        }

        // ADD COLUMN takes this lock anyway; taking it before the rows are read keeps a writer that does not take
        // the advisory lock, such as a host still on the old library, from adding a row in between.
        await using (var lockTable = new NpgsqlCommand(LockTableSql, connection, transaction))
        {
            await lockTable.ExecuteNonQueryAsync(cancellationToken);
        }

        var rows = await ReadRowsAsync(connection, transaction, cancellationToken);
        var attribution = Attribute(rows, sources);
        if (attribution.IsFailure)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result<int>.Failure(attribution.Error);
        }

        var assigned = attribution.Value;

        // SET NOT NULL also proves every row got its source: a row the UPDATE missed fails it, and nothing commits.
        await using (var upgrade = new NpgsqlCommand(UpgradeSql, connection, transaction))
        {
            upgrade.Parameters.Add(new NpgsqlParameter<int[]>("versions", assigned.Select(row => row.Version).ToArray()));
            upgrade.Parameters.Add(new NpgsqlParameter<string[]>("sources", assigned.Select(row => row.Source).ToArray()));
            await upgrade.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return Result<int>.Success(assigned.Count);
    }

    private static Result<List<(int Version, string Source)>> Attribute(
        List<(int Version, string Name)> rows,
        IReadOnlyList<IMigrationSource> sources)
    {
        var owners = new Dictionary<(int Version, string Name), List<string>>();
        foreach (var source in sources)
        {
            foreach (var migration in source.GetMigrations())
            {
                if (!owners.TryGetValue((migration.Version, migration.Name), out var names))
                {
                    names = [];
                    owners.Add((migration.Version, migration.Name), names);
                }

                names.Add(source.Name);
            }
        }

        var assigned = new List<(int Version, string Source)>(rows.Count);
        var unowned = new List<string>();
        var shared = new List<string>();
        foreach (var row in rows)
        {
            var label = row.Version.ToString(CultureInfo.InvariantCulture) + " '" + row.Name + "'";
            if (!owners.TryGetValue(row, out var names))
            {
                unowned.Add(label);
            }
            else if (names.Count > 1)
            {
                shared.Add(label + " (" + string.Join(", ", names) + ")");
            }
            else
            {
                assigned.Add((row.Version, names[0]));
            }
        }

        if (unowned.Count == 0 && shared.Count == 0)
        {
            return Result<List<(int Version, string Source)>>.Success(assigned);
        }

        var known = string.Join(", ", sources.Select(source => "'" + source.Name + "'"));
        var problems = new List<string>(2);
        if (unowned.Count > 0)
        {
            problems.Add($"{unowned.Count} rows are a migration of none of them: {string.Join(", ", unowned)}");
        }

        if (shared.Count > 0)
        {
            problems.Add($"{shared.Count} rows are a migration of more than one: {string.Join(", ", shared)}");
        }

        return Result<List<(int Version, string Source)>>.Failure(
            $"The migration history table {HistoryTable} predates source scoping and cannot be attributed to the " +
            $"sources {known} by version and name: {string.Join("; ", problems)}. The history table is unchanged.");
    }

    private static async Task<bool> IsUnscopedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        // The same lookup as PostgresMigrationDialect.SelectUnscopedHistorySql, in current_schema(), where an
        // unqualified CREATE TABLE puts the table, plus the table's own existence: that query reports an absent
        // table as unscoped, and a fresh database has nothing to upgrade.
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM information_schema.tables " +
            $"WHERE table_schema = current_schema() AND table_name = '{HistoryTable}') " +
            "AND NOT EXISTS (SELECT 1 FROM information_schema.columns " +
            $"WHERE table_schema = current_schema() AND table_name = '{HistoryTable}' AND column_name = 'source')",
            connection,
            transaction);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<List<(int Version, string Name)>> ReadRowsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var rows = new List<(int Version, string Name)>();
        await using var command = new NpgsqlCommand(
            $"SELECT version, name FROM {HistoryTable} ORDER BY version", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add((reader.GetInt32(0), reader.GetString(1)));
        }

        return rows;
    }
}
