using System.Data.Async.Adapters;
using Npgsql;
using Thalos.Workflow.Orm;
using ZeroAlloc.ORM.Migrations;
using ZeroAlloc.Outbox.Orm;
using ZeroAlloc.Results;

namespace Daedalus.Migrations;

/// <summary>What <see cref="WorkflowEngineMigrator.ApplyAsync" /> did.</summary>
/// <param name="UpgradedHistoryRows">
///     The pre-scoping history rows attributed to their source; zero once the table is scoped, or on a fresh
///     database.
/// </param>
/// <param name="Applied">The migrations applied by this run, outbox first, then workflow.</param>
public sealed record WorkflowEngineMigrationOutcome(int UpgradedHistoryRows, IReadOnlyList<Migration> Applied);

/// <summary>
///     The workflow engine's raw-SQL migrations, as <c>Daedalus.Migrations</c> applies them: the shared history
///     table upgraded from before source scoping first, then <c>OutboxOrmMigrations.Postgres</c>, then
///     <c>WorkflowOrmMigrations.Postgres</c>.
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
    ///     Attributes a pre-scoping history table's rows to their sources, then runs every source.
    /// </summary>
    /// <returns>
    ///     A failure, with nothing changed, when the pre-scoping table holds a row that is no source's migration.
    ///     A migration's own SQL failing still throws, as <see cref="MigrationRunner.RunAsync" /> does.
    /// </returns>
    public static async Task<Result<WorkflowEngineMigrationOutcome>> ApplyAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Before any runner: a runner upgrading the table itself assigns every row to its own source, and refuses
        // the table because the other source's rows are in it.
        var upgrade = await PreScopingMigrationHistory.UpgradeAsync(connection, Sources, cancellationToken);
        if (upgrade.IsFailure)
        {
            return Result<WorkflowEngineMigrationOutcome>.Failure(upgrade.Error);
        }

        var asyncConnection = connection.AsAsync();
        var dialect = new PostgresMigrationDialect();
        var applied = new List<Migration>();
        foreach (var source in Sources)
        {
            applied.AddRange(await new MigrationRunner(asyncConnection, source, dialect).RunAsync(cancellationToken));
        }

        return Result<WorkflowEngineMigrationOutcome>.Success(new WorkflowEngineMigrationOutcome(upgrade.Value, applied));
    }
}
