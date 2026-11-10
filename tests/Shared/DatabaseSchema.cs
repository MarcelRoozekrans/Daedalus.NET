using System.Data.Async.Adapters;
using Daedalus.Infrastructure.Persistence.Migrations;
using Daedalus.Migrations;
using Npgsql;

namespace Daedalus.Tests.Shared;

/// <summary>
///     Builds a test database's schema the way <c>Daedalus.Migrations</c> builds production's: the history table
///     upgraded from before source scoping, then the Daedalus migration source, then the workflow engine's sources,
///     in that order. Linked into each test
///     project that owns a Postgres fixture, so no fixture holds a schema that differs from what ships.
/// </summary>
internal static class DatabaseSchema
{
    /// <summary>
    ///     Applies every migration the host applies to the database at <paramref name="connectionString"/>. An empty
    ///     database gets the whole schema; applying it to an already migrated one changes nothing.
    /// </summary>
    /// <param name="connectionString">The database to build the schema in.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns>A task that completes when the schema is in place.</returns>
    /// <exception cref="InvalidOperationException">The history table upgrade was refused.</exception>
    public static async Task ApplyAsync(string connectionString, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        // The Daedalus baseline creates the pgvector extension that Rag.NET's rag_chunks and the agent memories need.
        // A history table from before source scoping is attributed to its sources before any runner sees it.
        var upgrade = await PreScopingMigrationHistory.UpgradeAsync(connection, WorkflowEngineMigrator.Sources, ct)
            .ConfigureAwait(false);
        if (upgrade.IsFailure)
        {
            throw new InvalidOperationException($"The migration history upgrade was refused: {upgrade.Error}");
        }

        await DaedalusMigrations.CreateRunner(connection.AsAsync()).RunAsync(ct).ConfigureAwait(false);

        await WorkflowEngineMigrator.ApplyAsync(connection, ct).ConfigureAwait(false);
    }
}
