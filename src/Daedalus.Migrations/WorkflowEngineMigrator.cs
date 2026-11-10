using System.Data.Async.Adapters;
using Npgsql;
using Thalos.Workflow.Orm;
using ZeroAlloc.ORM.Migrations;
using ZeroAlloc.Outbox.Orm;

namespace Daedalus.Migrations;

/// <summary>
///     The workflow engine's raw-SQL migrations, as <c>Daedalus.Migrations</c> applies them:
///     <c>OutboxOrmMigrations.Postgres</c>, then <c>WorkflowOrmMigrations.Postgres</c>. The host upgrades a history
///     table from before source scoping with <see cref="PreScopingMigrationHistory.UpgradeAsync" /> over
///     <see cref="Sources" /> before any runner, the Daedalus one included, because a runner given such a table
///     assigns all its rows to its own source.
/// </summary>
public static class WorkflowEngineMigrator
{
    /// <summary>
    ///     Every source that records into this database's <c>__zaorm_migrations</c>, in the order they run. The
    ///     outbox runs first: <c>OrmWorkflowStore</c> enqueues into its table in the same transaction as the
    ///     workflow tables it writes, which mirrors <c>WorkflowOrmSchemaInitializer</c>'s own ordering.
    /// </summary>
    public static IReadOnlyList<IMigrationSource> Sources { get; } =
        [OutboxOrmMigrations.Postgres, WorkflowOrmMigrations.Postgres];

    /// <summary>
    ///     Runs every source in <see cref="Sources" /> on a history table that is already scoped or absent.
    /// </summary>
    /// <returns>
    ///     The migrations applied by this run, outbox first, then workflow. A migration's own SQL failing throws, as
    ///     <see cref="MigrationRunner.RunAsync" /> does.
    /// </returns>
    public static async Task<IReadOnlyList<Migration>> ApplyAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var asyncConnection = connection.AsAsync();
        var dialect = new PostgresMigrationDialect();
        var applied = new List<Migration>();
        foreach (var source in Sources)
        {
            applied.AddRange(await new MigrationRunner(asyncConnection, source, dialect).RunAsync(cancellationToken));
        }

        return applied;
    }
}
