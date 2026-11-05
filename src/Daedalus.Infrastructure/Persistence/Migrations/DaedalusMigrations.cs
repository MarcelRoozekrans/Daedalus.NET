using System.Data.Async;
using ZeroAlloc.ORM;
using ZeroAlloc.ORM.Migrations;

namespace Daedalus.Infrastructure.Persistence.Migrations;

/// <summary>
///     Daedalus' own schema migrations, the SQL files embedded beside this class. Migration 1, <c>0001_baseline.sql</c>,
///     is the schema EF Core's chain leaves at <see cref="EfHeadMigrationId"/>: a new database runs it, and a database
///     EF built at that head records it as applied without running it.
/// </summary>
/// <remarks>
///     A database whose EF history ends anywhere else, older or empty, is refused with
///     <see cref="ZeroAllocOrmMigrationConflictException"/> before anything is recorded or
///     run. Bring it to <see cref="EfHeadMigrationId"/> with EF first. EF's chain is frozen at that id:
///     <c>__EFMigrationsHistory</c> stays, never written again, and every schema change from here on is a new file
///     here, numbered above the last.
/// </remarks>
public static class DaedalusMigrations
{
    /// <summary>
    ///     The source's name in <c>__zaorm_migrations</c>. Fixed for good: renaming it would make the runner treat
    ///     the source as new and run migration 1 again.
    /// </summary>
    public const string SourceName = "daedalus";

    /// <summary>The last EF Core migration; an EF database adopted by migration 1 must be at exactly this id.</summary>
    public const string EfHeadMigrationId = "20261014090300_AddNodeSelectionUniqueIndex";

    /// <summary>The manifest prefix of the embedded <c>NNNN_name.sql</c> files, the namespace of this folder.</summary>
    private const string ResourcePrefix = "Daedalus.Infrastructure.Persistence.Migrations.";

    /// <summary>The last version the schema of an EF database at <see cref="EfHeadMigrationId"/> reflects.</summary>
    private const int BaselineVersion = 1;

    /// <summary>
    ///     Creates the runner for Daedalus' migrations over <paramref name="connection"/>, with the EF Core baseline.
    /// </summary>
    /// <param name="connection">An open connection to the Daedalus database. The caller keeps ownership.</param>
    /// <returns>The runner; <see cref="MigrationRunner.RunAsync"/> applies what the database lacks.</returns>
    public static MigrationRunner CreateRunner(IAsyncDbConnection connection) =>
        new(
            connection,
            new EmbeddedResourceMigrationSource(typeof(DaedalusMigrations).Assembly, ResourcePrefix) { Name = SourceName },
            new PostgresMigrationDialect(),
            new MigrationBaseline(
                throughVersion: BaselineVersion,
                EfCoreBaseline.LatestMigrationIs(EfHeadMigrationId, SqlDialect.PostgreSql)));
}
