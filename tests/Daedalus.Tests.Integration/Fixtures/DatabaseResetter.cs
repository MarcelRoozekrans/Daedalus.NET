#pragma warning disable S1133 // SonarSource rule - deprecated code
using Npgsql;

namespace Daedalus.Tests.Integration.Fixtures;

/// <summary>
///     Utility for resetting PostgreSQL test database between test runs.
///     DEPRECATED: Use RespawnDatabaseResetter through PostgresFixture.DatabaseResetter instead.
///     This class is maintained for backward compatibility only.
/// </summary>
[Obsolete("Use RespawnDatabaseResetter through fixture.DatabaseResetter instead", false)]
public static class DatabaseResetter
{
    /// <summary>
    ///     Resets the test database by truncating all tables.
    ///     DEPRECATED: Use fixture.DatabaseResetter.ResetAsync() instead.
    /// </summary>
    [Obsolete("Use fixture.DatabaseResetter.ResetAsync() instead", false)]
    public static async Task ResetAsync(string connectionString)
    {
        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        try
        {
            // Disable foreign key constraints temporarily
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SET session_replication_role = 'replica'";
                await command.ExecuteNonQueryAsync();
            }

            // Get all tables
            var tables = new List<string>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
                    SELECT tablename FROM pg_tables 
                    WHERE schemaname = 'public' 
                    AND tablename NOT LIKE 'pg_%'
                    ORDER BY tablename";

                try
                {
                    using var reader = await command.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        tables.Add(reader.GetString(0));
                    }
                }
                catch (Exception)
                {
                    // If tables query fails (no tables yet), just skip truncation
                    tables.Clear();
                }
            }

            // Truncate each table
            foreach (var table in tables)
            {
                try
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = $"TRUNCATE TABLE \"{table}\" RESTART IDENTITY CASCADE";
                    await command.ExecuteNonQueryAsync();
                }
                catch (Exception)
                {
                    // Skip any tables that fail to truncate (system tables, etc.)
                }
            }

            // Re-enable foreign key constraints
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SET session_replication_role = 'origin'";
                await command.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            await connection.CloseAsync();
        }
    }
}
