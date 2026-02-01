namespace Daedalus.Infrastructure.Persistence;

/// <summary>
///     Centralized database configuration settings.
/// </summary>
public static class DatabaseSettings
{
    /// <summary>
    ///     Default database host.
    /// </summary>
    public const string DefaultHost = "localhost";

    /// <summary>
    ///     Default database port.
    /// </summary>
    public const int DefaultPort = 5432;

    /// <summary>
    ///     Default database name.
    /// </summary>
    public const string DefaultDatabase = "daedalus";

    /// <summary>
    ///     Default database username.
    /// </summary>
    public const string DefaultUsername = "postgres";

    /// <summary>
    ///     Default database password.
    /// </summary>
    public const string DefaultPassword = "postgres";

    /// <summary>
    ///     Build a connection string from individual components.
    /// </summary>
    public static string BuildConnectionString(
        string host = DefaultHost,
        int port = DefaultPort,
        string database = DefaultDatabase,
        string username = DefaultUsername,
        string password = DefaultPassword,
        bool includeErrorDetail = true)
    {
        return $"Host={host};Port={port};Database={database};Username={username};Password={password}" +
               (includeErrorDetail ? ";Include Error Detail=true" : "");
    }

    /// <summary>
    ///     Build the default connection string.
    /// </summary>
    public static string GetDefaultConnectionString() =>
        BuildConnectionString();
}
