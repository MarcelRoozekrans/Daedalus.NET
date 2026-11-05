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

            // A broken catalog query would leave both sides empty in its category and pass; the EF chain has each kind.
            foreach (var category in new[] { "column ", "index ", "constraint ", "sequence ", "extension " })
            {
                ef.Should().Contain(s => s.StartsWith(category, StringComparison.Ordinal), "the snapshot must read every {0}category", category);
            }

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
        // A sequence is named with the table that owns it, through an identity or serial column, if any.
        const string Sequences =
            "SELECT 'sequence ' || s.sequencename || ' owner=' || coalesce(t.relname, '-') || ' ' || s.data_type"
            + " || ' start=' || s.start_value || ' inc=' || s.increment_by || ' min=' || s.min_value"
            + " || ' max=' || s.max_value || ' cycle=' || s.cycle"
            + " FROM pg_sequences s"
            + " JOIN pg_class q ON q.relname = s.sequencename AND q.relnamespace = 'public'::regnamespace"
            + " LEFT JOIN pg_depend d ON d.classid = 'pg_class'::regclass AND d.objid = q.oid"
            + " AND d.refclassid = 'pg_class'::regclass AND d.deptype IN ('a', 'i')"
            + " LEFT JOIN pg_class t ON t.oid = d.refobjid"
            + " WHERE s.schemaname = 'public' AND (t.relname IS NULL OR t.relname NOT IN " + Excluded + ")";
        const string Extensions = "SELECT 'extension ' || extname || ' ' || extversion FROM pg_extension";

        var rows = new List<string>();
        foreach (var sql in new[] { Columns, Indexes, Constraints, Sequences, Extensions })
        {
            rows.AddRange(await EfChainDatabase.ReadStringsAsync(connectionString, sql, ct));
        }

        rows.Sort(StringComparer.Ordinal);
        return rows;
    }
}
