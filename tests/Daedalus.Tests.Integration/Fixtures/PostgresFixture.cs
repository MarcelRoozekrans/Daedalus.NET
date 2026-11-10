using Daedalus.Infrastructure.Persistence.Orm;
using Daedalus.Tests.Shared;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Daedalus.Tests.Integration.Fixtures;

/// <summary>
///     PostgreSQL test container fixture for integration tests.
///     Provides a real PostgreSQL database instance for testing.
///     Handles container startup with retry logic for reliability.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const int _startupRetries = 3;
    private const int _retryDelayMs = 500;
    private PostgreSqlContainer? _container;
    private RespawnDatabaseResetter? _databaseResetter;
    private NpgsqlDataSource? _dataSource;
    private DaedalusDb? _db;

    /// <summary>
    ///     Gets the connection string for the test database.
    /// </summary>
    public string ConnectionString
    {
        get
        {
            if (_container == null)
            {
                throw new InvalidOperationException("Container not initialized. Call InitializeAsync first.");
            }

            return _container.GetConnectionString();
        }
    }

    /// <summary>
    ///     Gets the PostgreSQL host.
    /// </summary>
    public string Host => _container?.Hostname ?? "localhost";

    /// <summary>
    ///     Gets the database resetter for test cleanup.
    /// </summary>
    public RespawnDatabaseResetter DatabaseResetter
    {
        get
        {
            if (_databaseResetter == null)
            {
                throw new InvalidOperationException("Database resetter not initialized. Call InitializeAsync first.");
            }

            return _databaseResetter;
        }
    }

    /// <summary>
    ///     Gets the <see cref="DaedalusDb"/> over the fixture database, the way a host's ZeroAlloc.ORM stores reach it:
    ///     one data source for the fixture's lifetime, a pooled connection per call.
    /// </summary>
    public DaedalusDb Db => _db ?? throw new InvalidOperationException("Container not initialized. Call InitializeAsync first.");

    /// <summary>
    ///     Gets the fixture's one <see cref="NpgsqlDataSource"/>, the one <see cref="Db"/> opens from. A test provider
    ///     registers this instance, so the container does not dispose it.
    /// </summary>
    public NpgsqlDataSource DataSource =>
        _dataSource ?? throw new InvalidOperationException("Container not initialized. Call InitializeAsync first.");

    /// <summary>
    ///     Starts the PostgreSQL container with retry logic.
    /// </summary>
    public async Task InitializeAsync()
    {
#pragma warning disable CS0618 // Using PostgreSqlBuilder with image parameter - obsolete warning for parameterless constructor
        // pgvector image: Rag.NET's rag_chunks (Thalos memory index) needs the pgvector extension
        _container = new PostgreSqlBuilder()
            .WithImage("pgvector/pgvector:pg16")
            .WithDatabase("daedalus_test")
            .WithUsername("test")
            .WithPassword("test")
            .WithCleanUp(true)
            .Build();
#pragma warning restore CS0618

        // Retry logic for container startup
        var attempt = 0;
        Exception? lastException = null;

        while (attempt < _startupRetries)
        {
            try
            {
                await _container.StartAsync();
                // Verify connection works
                await VerifyConnectionAsync();
                // Apply database migrations
                await ApplyMigrationsAsync();
                // Initialize Respawn checkpoint after schema is created
                await InitializeRespawnerAsync();
                _dataSource = NpgsqlDataSource.Create(ConnectionString);
                _db = new DaedalusDb(_dataSource);
                return;
            }
            catch (Exception ex)
            {
                lastException = ex;
                attempt++;

                if (attempt < _startupRetries)
                {
                    await Task.Delay(_retryDelayMs * attempt); // Exponential backoff
                }
            }
        }

        throw new InvalidOperationException(
            $"Failed to start PostgreSQL container after {_startupRetries} attempts. " +
            $"Make sure Docker is running and has sufficient resources.",
            lastException);
    }

    /// <summary>
    ///     Stops and disposes the PostgreSQL container.
    /// </summary>
    public async Task DisposeAsync()
    {
        if (_dataSource != null)
        {
            await _dataSource.DisposeAsync();
        }

        if (_container != null)
        {
            try
            {
                await _container.StopAsync();
                await _container.DisposeAsync();
            }
            catch (Exception ex)
            {
                // Log but don't throw - we want cleanup to complete
                System.Console.WriteLine($"Warning: Error during PostgreSQL container cleanup: {ex.Message}");
            }
        }
    }

    /// <summary>
    ///     Applies the real migrations to the test database.
    /// </summary>
    private async Task ApplyMigrationsAsync()
    {
        try
        {
            // The schema Daedalus.Migrations builds in production: the Daedalus migration source, then the workflow
            // engine's.
            await DatabaseSchema.ApplyAsync(ConnectionString, CancellationToken.None);
            System.Console.WriteLine("✓ Database schema created successfully");
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"✗ Migration error: {ex.Message}");
            System.Console.WriteLine($"  Stack: {ex.StackTrace}");
            throw new InvalidOperationException("Failed to create database schema", ex);
        }
    }

    /// <summary>
    ///     Verifies that the database connection is working.
    /// </summary>
    private async Task VerifyConnectionAsync()
    {
        try
        {
            using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await connection.CloseAsync();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to verify PostgreSQL connection", ex);
        }
    }

    /// <summary>
    ///     Initializes the Respawn database resetter after migrations are applied.
    /// </summary>
    private async Task InitializeRespawnerAsync()
    {
        try
        {
            _databaseResetter = new RespawnDatabaseResetter(ConnectionString);
            await _databaseResetter.InitializeAsync();
            System.Console.WriteLine("✓ Respawn checkpoint created successfully");
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"✗ Respawn initialization error: {ex.Message}");
            throw new InvalidOperationException("Failed to initialize Respawn database resetter", ex);
        }
    }
}
