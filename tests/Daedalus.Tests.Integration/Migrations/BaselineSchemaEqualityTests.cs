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
    ///     sequences; and extensions, all equal. Migration 1 alone is compared, executed straight from its embedded
    ///     file and not through the runner, which would go on to run migration 2 and drop the six columns the EF chain
    ///     still has. Red: delete one index from <c>0001_baseline.sql</c>; the index is listed as only in the EF
    ///     database.
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
            await EfChainDatabase.ExecuteAsync(freshDatabase, ReadEmbedded("0001_baseline.sql"), ct);

            var ef = await SchemaAsync(efDatabase, ct);
            var fresh = await SchemaAsync(freshDatabase, ct);
            AssertEqual(ef, fresh, "the baseline must build everything the EF chain builds", "the baseline must build nothing the EF chain does not");
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

    /// <summary>
    ///     After every migration, a fresh database and an adopted EF database have the same schema: migration 2 drops the
    ///     six columns the same way on both, so the two paths never drift apart. Red: remove one ALTER TABLE from
    ///     <c>0002_drop_dead_rowversions.sql</c>; the column is listed as only in the adopted database.
    /// </summary>
    [Fact]
    public async Task Fresh_database_matches_an_adopted_ef_database_after_every_migration()
    {
        var ct = CancellationToken.None;
        var adopted = await EfChainDatabase.CreateAsync(fixture.ConnectionString, ct);
        string? fresh = null;
        try
        {
            fresh = await EfChainDatabase.CreateEmptyAsync(fixture.ConnectionString, ct);
            foreach (var (database, expected) in new[] { (adopted, new[] { 2 }), (fresh, new[] { 1, 2 }) })
            {
                await using var connection = new NpgsqlConnection(database);
                await connection.OpenAsync(ct);
                var applied = await DaedalusMigrations.CreateRunner(connection.AsAsync()).RunAsync(ct);
                applied.Select(m => m.Version).Should().Equal(expected);
            }

            var adoptedSchema = await SchemaAsync(adopted, ct);
            adoptedSchema.Should().NotContain(
                s => s.Contains("column Projects.RowVersion", StringComparison.Ordinal),
                "migration 2 drops the dead column on an adopted database");
            AssertEqual(
                adoptedSchema,
                await SchemaAsync(fresh, ct),
                "a new database must have everything an adopted one has",
                "a new database must have nothing an adopted one lacks");
        }
        finally
        {
            await EfChainDatabase.DropAsync(fixture.ConnectionString, adopted);
            if (fresh is not null)
            {
                await EfChainDatabase.DropAsync(fixture.ConnectionString, fresh);
            }
        }
    }

    private static string ReadEmbedded(string file)
    {
        using var stream = typeof(DaedalusMigrations).Assembly
            .GetManifestResourceStream("Daedalus.Infrastructure.Persistence.Migrations." + file)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void AssertEqual(List<string> left, List<string> right, string onlyLeftReason, string onlyRightReason)
    {
        // A broken catalog query would leave both sides empty in its category and pass; each side has every kind.
        foreach (var category in new[] { "column ", "index ", "constraint ", "sequence ", "extension " })
        {
            left.Should().Contain(s => s.StartsWith(category, StringComparison.Ordinal), "the snapshot must read every {0}category", category);
        }

        // Joined, so a failure lists every difference rather than the first.
        var onlyLeft = string.Join(Environment.NewLine, left.Except(right, StringComparer.Ordinal));
        var onlyRight = string.Join(Environment.NewLine, right.Except(left, StringComparer.Ordinal));
        onlyLeft.Should().BeEmpty(onlyLeftReason);
        onlyRight.Should().BeEmpty(onlyRightReason);
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
