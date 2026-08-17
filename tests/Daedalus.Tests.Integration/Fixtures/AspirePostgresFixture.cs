using Aspire.Hosting;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Daedalus.Tests.Integration.Fixtures;

/// <summary>
///     Aspire-based PostgreSQL test fixture using Testcontainers.
///     Provides integrated container orchestration for testing.
/// </summary>
public sealed class AspirePostgresFixture : IAsyncLifetime
{
#pragma warning disable CS0618 // Using PostgreSqlBuilder with image parameter - obsolete warning for parameterless constructor
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg16")
        .WithDatabase("daedalus_test")
        .WithUsername("test")
        .WithPassword("test")
        .WithCleanUp(true)
        .Build();
#pragma warning restore CS0618

    private DistributedApplication? _app;
    private RespawnDatabaseResetter? _databaseResetter;

    /// <summary>
    ///     Gets the connection string for the test database.
    /// </summary>
    public string ConnectionString => _container.GetConnectionString();

    /// <summary>
    ///     Gets the PostgreSQL host.
    /// </summary>
    public string Host => _container.Hostname;

    /// <summary>
    ///     Gets the PostgreSQL port.
    /// </summary>
    public ushort Port => _container.GetMappedPublicPort(5432);

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
    ///     Initializes and starts the PostgreSQL container and Aspire application.
    /// </summary>
    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // Initialize Aspire app context if needed for integration tests
        var builder = DistributedApplication.CreateBuilder();
        _app = builder.Build();

        // Apply database migrations and initialize Respawn checkpoint
        await ApplyMigrationsAsync();
        await InitializeRespawnerAsync();
    }

    /// <summary>
    ///     Stops and disposes the PostgreSQL container and Aspire application.
    /// </summary>
    public async Task DisposeAsync()
    {
        if (_app != null)
        {
            await _app.StopAsync();
        }

        await _container.DisposeAsync();
    }

    /// <summary>
    ///     Applies database migrations to the test database.
    /// </summary>
    private async Task ApplyMigrationsAsync()
    {
        try
        {
            var options = PostgresFixture.CreateDbContextOptions(ConnectionString);

            using var dbContext = new ApplicationDbContext(options);
            // EnsureCreatedAsync does not run migration SQL, so create the pgvector extension explicitly
            await dbContext.Database.ExecuteSqlRawAsync("CREATE EXTENSION IF NOT EXISTS vector;");
            await dbContext.Database.EnsureCreatedAsync();
            System.Console.WriteLine("✓ Database schema created successfully (Aspire fixture)");
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"✗ Migration error (Aspire fixture): {ex.Message}");
            throw new InvalidOperationException("Failed to create database schema", ex);
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
            System.Console.WriteLine("✓ Respawn checkpoint created successfully (Aspire fixture)");
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"✗ Respawn initialization error (Aspire fixture): {ex.Message}");
            throw new InvalidOperationException("Failed to initialize Respawn database resetter", ex);
        }
    }
}
