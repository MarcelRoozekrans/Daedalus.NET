using System.Data.Async.Adapters;
using Daedalus.Infrastructure.Persistence.Migrations;
using Daedalus.Tests.Integration.Fixtures;
using Npgsql;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Migrations;

/// <summary>
///     Phase 3.5, Task 2: the Daedalus baseline, migration 1, builds exactly the schema EF Core's frozen chain builds.
///     A drift would split new databases from adopted ones for good, since an adopted database never runs migration 1.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class BaselineSchemaEqualityTests(PostgresFixture fixture)
{
    // The two history tables differ by design: EF's stays on an adopted database, the runner's is on both.
    private const string Excluded = "('__EFMigrationsHistory', '__zaorm_migrations')";

    /// <summary>
    ///     Columns with their order, type, nullability, default and identity; index definitions; constraint definitions;
    ///     sequences; and extensions, all equal. Red: delete one index from <c>0001_baseline.sql</c>; the index is
    ///     listed as only in the EF database.
    /// </summary>
    [Fact]
    public async Task Fresh_baseline_matches_the_ef_chain()
    {
        var ct = CancellationToken.None;
        var efDatabase = await EfChainDatabase.CreateAsync(fixture.ConnectionString, ct);
        string? freshDatabase = null;
        try
        {
            freshDatabase = await EfChainDatabase.CreateEmptyAsync(fixture.ConnectionString, ct);
            await using (var connection = new NpgsqlConnection(freshDatabase))
            {
                await connection.OpenAsync(ct);
                var applied = await DaedalusMigrations.CreateRunner(connection.AsAsync()).RunAsync(ct);
                applied.Select(m => m.Version).Should().Equal(1);
            }

            var ef = await SchemaAsync(efDatabase, ct);
            var fresh = await SchemaAsync(freshDatabase, ct);

            ef.Should().Contain(s => s.StartsWith("index ", StringComparison.Ordinal), "the snapshot must read the schema");
            // Joined, so a failure lists every difference rather than the first.
            var onlyEf = string.Join(Environment.NewLine, ef.Except(fresh, StringComparer.Ordinal));
            var onlyFresh = string.Join(Environment.NewLine, fresh.Except(ef, StringComparer.Ordinal));
            onlyEf.Should().BeEmpty("the baseline must build everything the EF chain builds");
            onlyFresh.Should().BeEmpty("the baseline must build nothing the EF chain does not");
        }
        finally
        {
            await EfChainDatabase.DropAsync(fixture.ConnectionString, efDatabase);
            if (freshDatabase is not null)
            {
                await EfChainDatabase.DropAsync(fixture.ConnectionString, freshDatabase);
            }
        }
    }

    private static async Task<List<string>> SchemaAsync(string connectionString, CancellationToken ct)
    {
        // Column order is the rank among live columns: a dropped column leaves a hole in ordinal_position.
        const string Columns =
            "SELECT 'column ' || table_name || '.' || column_name"
            + " || ' #' || row_number() OVER (PARTITION BY table_name ORDER BY ordinal_position)"
            + " || ' ' || data_type || ' ' || udt_name"
            + " || ' len=' || coalesce(character_maximum_length::text, '-')"
            + " || ' prec=' || coalesce(numeric_precision::text, '-') || ',' || coalesce(numeric_scale::text, '-')"
            + " || ' dt=' || coalesce(datetime_precision::text, '-') || ' ' || coalesce(interval_type, '-')"
            + " || ' nullable=' || is_nullable"
            + " || ' default=' || coalesce(column_default, '-')"
            + " || ' identity=' || is_identity || ' ' || coalesce(identity_generation, '-')"
            + " FROM information_schema.columns"
            + " WHERE table_schema = 'public' AND table_name NOT IN " + Excluded;
        const string Indexes =
            "SELECT 'index ' || indexdef FROM pg_indexes"
            + " WHERE schemaname = 'public' AND tablename NOT IN " + Excluded;
        const string Constraints =
            "SELECT 'constraint ' || t.relname || '.' || c.conname || ' ' || pg_get_constraintdef(c.oid)"
            + " FROM pg_constraint c JOIN pg_class t ON t.oid = c.conrelid"
            + " WHERE c.connamespace = 'public'::regnamespace AND t.relname NOT IN " + Excluded;
        const string Sequences =
            "SELECT 'sequence ' || sequencename || ' ' || data_type || ' start=' || start_value"
            + " || ' inc=' || increment_by || ' min=' || min_value || ' max=' || max_value || ' cycle=' || cycle"
            + " FROM pg_sequences WHERE schemaname = 'public'";
        const string Extensions = "SELECT 'extension ' || extname || ' ' || extversion FROM pg_extension";

        var rows = new List<string>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        foreach (var sql in new[] { Columns, Indexes, Constraints, Sequences, Extensions })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(reader.GetString(0));
            }
        }

        rows.Sort(StringComparer.Ordinal);
        return rows;
    }
}
