using System.Data.Async.Adapters;
using Daedalus.Infrastructure.Persistence.Migrations;
using Daedalus.Migrations;
using Npgsql;

namespace Daedalus.Tests.Shared;

/// <summary>
///     Builds a test database's schema the way <c>Daedalus.Migrations</c> builds production's: the pgvector extension,
///     then the Daedalus migration source, then the workflow engine's sources, in that order. Linked into each test
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
    /// <exception cref="InvalidOperationException">The workflow engine's migration was refused.</exception>
    public static async Task ApplyAsync(string connectionString, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        // Rag.NET's rag_chunks (Thalos memory index) and the agent memories need pgvector. The baseline creates it
        // for a new database too; creating it first keeps the order Daedalus.Migrations is specified to run in.
        await using (var extension = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS vector", connection))
        {
            await extension.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await DaedalusMigrations.CreateRunner(connection.AsAsync()).RunAsync(ct).ConfigureAwait(false);

        var workflow = await WorkflowEngineMigrator.ApplyAsync(connection, ct).ConfigureAwait(false);
        if (workflow.IsFailure)
        {
            throw new InvalidOperationException($"The workflow engine migration was refused: {workflow.Error}");
        }
    }
}
