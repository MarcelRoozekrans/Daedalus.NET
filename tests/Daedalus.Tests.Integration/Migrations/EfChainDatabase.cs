using Daedalus.Infrastructure.Persistence.Migrations;
using Npgsql;

namespace Daedalus.Tests.Integration.Migrations;

/// <summary>
///     Builds throwaway databases on the fixture's server: an "EF database" from <c>ef-chain.sql</c>, EF Core's 26
///     migrations scripted once with <c>dotnet ef migrations script 0 20261014090300_AddNodeSelectionUniqueIndex</c>,
///     history inserts included; or an empty one. The script is frozen: it stands for every database EF built, so it is
///     never regenerated once ZeroAlloc.ORM owns the schema.
/// </summary>
public static class EfChainDatabase
{
    private static readonly string _scriptPath = Path.Combine(AppContext.BaseDirectory, "Migrations", "ef-chain.sql");

    /// <summary>Creates a database and runs EF's frozen chain on it.</summary>
    /// <param name="adminConnectionString">A connection string to the server, allowed to create databases.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns>The new database's connection string. Drop it with <see cref="DropAsync"/>.</returns>
    public static async Task<string> CreateAsync(string adminConnectionString, CancellationToken ct)
    {
        var connectionString = await CreateEmptyAsync(adminConnectionString, ct);
        var script = await File.ReadAllTextAsync(_scriptPath, ct);
        await ExecuteAsync(connectionString, script, ct);
        return connectionString;
    }

    /// <summary>
    ///     Undoes the head migration, <c>AddNodeSelectionUniqueIndex</c>, on an EF database, as
    ///     <c>dotnet ef database update</c> to its predecessor would: its index dropped, its history row deleted. The
    ///     result is a database whose EF head is not <see cref="DaedalusMigrations.EfHeadMigrationId"/>.
    /// </summary>
    /// <param name="connectionString">A database <see cref="CreateAsync"/> returned.</param>
    /// <param name="ct">Cancels the work.</param>
    public static Task UndoHeadMigrationAsync(string connectionString, CancellationToken ct) =>
        ExecuteAsync(
            connectionString,
            "DROP INDEX \"UX_WorkflowRunRecords_NodeSelection_RunId_Seq\"; "
            + "DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '" + DaedalusMigrations.EfHeadMigrationId + "'",
            ct);

    /// <summary>Creates an empty database.</summary>
    /// <param name="adminConnectionString">A connection string to the server, allowed to create databases.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns>The new database's connection string. Drop it with <see cref="DropAsync"/>.</returns>
    public static async Task<string> CreateEmptyAsync(string adminConnectionString, CancellationToken ct)
    {
        var name = $"adopt_{Guid.NewGuid():N}";
        await ExecuteAsync(adminConnectionString, $"CREATE DATABASE \"{name}\"", ct);
        return new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = name }.ConnectionString;
    }

    /// <summary>Drops a database made here, closing its pooled connections first.</summary>
    /// <param name="adminConnectionString">The connection string the database was created with.</param>
    /// <param name="connectionString">The connection string <see cref="CreateAsync"/> returned.</param>
    public static async Task DropAsync(string adminConnectionString, string connectionString)
    {
        var name = new NpgsqlConnectionStringBuilder(connectionString).Database;
        using (var pooled = new NpgsqlConnection(connectionString))
        {
            NpgsqlConnection.ClearPool(pooled);
        }

        await ExecuteAsync(adminConnectionString, $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", CancellationToken.None);
    }

    /// <summary>Runs <paramref name="sql"/>, which may hold several statements, as one batch.</summary>
    /// <param name="connectionString">The database to run it on.</param>
    /// <param name="sql">The SQL.</param>
    /// <param name="ct">Cancels the work.</param>
    public static async Task ExecuteAsync(string connectionString, string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Runs a query whose first column is text and returns that column of every row, in the order read.</summary>
    /// <param name="connectionString">The database to read.</param>
    /// <param name="sql">The query.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns>The first column of each row.</returns>
    public static async Task<IReadOnlyList<string>> ReadStringsAsync(string connectionString, string sql, CancellationToken ct)
    {
        var rows = new List<string>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }
}
