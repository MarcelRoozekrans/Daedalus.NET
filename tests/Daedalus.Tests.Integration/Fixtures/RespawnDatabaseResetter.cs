using Npgsql;
using Respawn;
using Table = Respawn.Graph.Table;

namespace Daedalus.Tests.Integration.Fixtures;

/// <summary>
///     Database resetter using Respawn library for efficient test database cleanup.
///     Creates a checkpoint of the database schema and resets to it between tests.
/// </summary>
public sealed class RespawnDatabaseResetter
{
    // Both history tables hold the schema's state, not test data: __zaorm_migrations is the runner's, and
    // __EFMigrationsHistory stays from EF's chain on a database that has one.
    private static readonly Table[] TablesToIgnoreArray = [new("__EFMigrationsHistory"), new("__zaorm_migrations")];
    private readonly string _connectionString;
    private Respawner? _respawner;

    public RespawnDatabaseResetter(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <summary>
    ///     Initializes the respawner by creating a checkpoint of the current database state.
    ///     Should be called once after migrations are applied.
    /// </summary>
    public async Task InitializeAsync()
    {
        using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        try
        {
            _respawner = await Respawner.CreateAsync(connection,
                new RespawnerOptions
                {
                    DbAdapter = DbAdapter.Postgres,
                    SchemasToInclude = ["public"],
                    TablesToIgnore = TablesToIgnoreArray
                });
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    /// <summary>
    ///     Resets the database to the checkpoint state by deleting all data
    ///     while preserving the schema.
    /// </summary>
    public async Task ResetAsync()
    {
        if (_respawner == null)
        {
            throw new InvalidOperationException("Respawner not initialized. Call InitializeAsync first.");
        }

        using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        try
        {
            await _respawner.ResetAsync(connection);
        }
        finally
        {
            await connection.CloseAsync();
        }
    }
}
