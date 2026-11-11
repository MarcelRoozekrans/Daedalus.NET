using System.Data.Common;
using System.Net;
using System.Net.Sockets;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence.Orm;
using Daedalus.Tests.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
// Alias to resolve ambiguity between Daedalus.Api.Program and other hosts' Program types
using ApiProgram = Daedalus.Api.Program;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Playwright.Api;

/// <summary>
///     The Api the suite runs against, in one of two modes.
///     <list type="bullet">
///         <item>
///             In-process (the default): a Postgres container and the Api in a <see cref="WebApplicationFactory{TEntryPoint}"/>
///             with <see cref="ApiTestOverrides"/> applied; the tests reach it over the in-memory TestServer.
///         </item>
///         <item>
///             External, when <see cref="BaseUrlVariable"/> is set: a host already running at that URL, such as the
///             natively published <c>Daedalus.Tests.NativeApiHost</c>, over the database
///             <see cref="ConnectionStringVariable"/> names. Neither a container nor a host starts here. That database
///             must be migrated before the host starts, since the host's hosted services read it on start; this only seeds it.
///         </item>
///     </list>
/// </summary>
/// <param name="environment">Reads an environment variable; the fixture passes the process environment.</param>
internal sealed class E2EServer(Func<string, string?> environment) : IAsyncDisposable
{
    /// <summary>The URL of an already running host; set, the suite runs against it.</summary>
    public const string BaseUrlVariable = "E2E_BASE_URL";

    /// <summary>The database of the host at <see cref="BaseUrlVariable"/>; required with it.</summary>
    public const string ConnectionStringVariable = "E2E_CONNECTION_STRING";

    private const string ConnectionStringEnvironmentVariable = "ConnectionStrings__daedalus";

    private PostgreSqlContainer? _postgresContainer;
    private WebApplicationFactory<ApiProgram>? _factory;
    private bool _setConnectionStringVariable;

    /// <summary>The base URL of the Api: the external host's, or a placeholder the in-memory TestServer ignores.</summary>
    public Uri? ServerUrl { get; private set; }

    /// <summary>The connection string for the test database.</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>Whether the Api answered its health check, so tests can run.</summary>
    public bool IsReady { get; private set; }

    /// <summary>The port of <see cref="ServerUrl"/>.</summary>
    public int Port { get; private set; }

    /// <summary>Whether this server started the Api in this process.</summary>
    public bool HostsInProcess => _factory is not null;

    /// <summary>Whether this server started, and so owns, a database container.</summary>
    public bool OwnsDatabaseContainer => _postgresContainer is not null;

    /// <summary>Creates an HttpClient for the Api, whichever mode serves it.</summary>
    public HttpClient CreateClient()
    {
        if (_factory is not null)
        {
            return _factory.CreateClient();
        }

        if (ServerUrl is not null)
        {
            return new HttpClient { BaseAddress = ServerUrl };
        }

        throw new InvalidOperationException(
            "Test server is not initialized. Ensure E2EServerFixture has completed setup.");
    }

    /// <summary>Starts or connects to the Api and waits until it answers its health check.</summary>
    public async Task StartAsync()
    {
        var externalUrl = environment(BaseUrlVariable);
        if (string.IsNullOrEmpty(externalUrl))
        {
            await StartInProcessAsync().ConfigureAwait(false);
        }
        else
        {
            await ConnectExternalAsync(externalUrl).ConfigureAwait(false);
        }

        using var client = CreateClient();
        var response = await client.GetAsync(new Uri("/health", UriKind.Relative)).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        IsReady = true;
    }

    private async Task ConnectExternalAsync(string externalUrl)
    {
        var connectionString = environment(ConnectionStringVariable);
        if (string.IsNullOrEmpty(connectionString))
        {
            throw new InvalidOperationException(
                $"{BaseUrlVariable} is set, so {ConnectionStringVariable} must name the database the host at {externalUrl} uses.");
        }

        ServerUrl = new Uri(externalUrl, UriKind.Absolute);
        Port = ServerUrl.Port;
        ConnectionString = connectionString;

        // The host is already running, so its database is already migrated; the suite's seed data is all that may be missing.
        await SeedTestDataAsync(ConnectionString).ConfigureAwait(false);
    }

    private async Task StartInProcessAsync()
    {
        // Start PostgreSQL container
#pragma warning disable CS0618 // Using PostgreSqlBuilder - obsolete warning for parameterless constructor
        _postgresContainer = new PostgreSqlBuilder()
            .WithImage("pgvector/pgvector:pg16")
            .WithDatabase("daedalus_e2e_test")
            .WithUsername("e2e_user")
            .WithPassword("e2e_password")
            .Build();
#pragma warning restore CS0618

        await _postgresContainer.StartAsync().ConfigureAwait(false);
        ConnectionString = _postgresContainer.GetConnectionString();

        // Set connection string as environment variable BEFORE creating WebApplicationFactory
        Environment.SetEnvironmentVariable(ConnectionStringEnvironmentVariable, ConnectionString);
        _setConnectionStringVariable = true;

        // Find an available port (kept for ServerUrl compatibility)
        Port = GetAvailablePort();
        ServerUrl = new Uri($"http://localhost:{Port}");

        // Create the database schema and seed data BEFORE the WebApplicationFactory host ever starts.
        // WebApplicationFactory starts the whole host - including hosted services such as Thalos's
        // SkillSyncService, which queries the Skills table - the first time _factory.Services (or
        // CreateClient()) is touched below. Doing schema setup afterwards raced that startup and
        // failed with "relation Skills does not exist" because the host's hosted services ran against
        // an empty database. Building the schema and the seed over a standalone data source here, before the
        // factory exists at all, guarantees the schema is in place first.
        // The real migrations, the schema Daedalus.Migrations builds in production, workflow tables included.
        await DatabaseSchema.ApplyAsync(ConnectionString, CancellationToken.None).ConfigureAwait(false);
        await SeedTestDataAsync(ConnectionString).ConfigureAwait(false);

        // Create and configure the WebApplicationFactory (uses in-memory TestServer)
        _factory = new WebApplicationFactory<ApiProgram>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                ApiTestOverrides.ConfigureHost(builder, ConnectionString);
                builder.ConfigureServices(ApiTestOverrides.Apply);
            });
    }

    private static async Task SeedTestDataAsync(string connectionString)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        try
        {
            await using (var any = dataSource.CreateCommand("SELECT EXISTS (SELECT 1 FROM \"Projects\")"))
            {
                if ((bool)(await any.ExecuteScalarAsync().ConfigureAwait(false))!)
                {
                    return;
                }
            }

            var projectResult = Project.Create(
                Guid.NewGuid(),
                "Test Project",
                "A test project for E2E testing");

            if (projectResult.IsFailure)
            {
                return;
            }

            var project = projectResult.Value;

            var task1Result = Domain.Entities.Task.Create(
                Guid.NewGuid(),
                project.Id,
                "TASK-001",
                "Implement feature A",
                "Complete the implementation of feature A",
                Priority.Medium,
                "Backend",
                1,
                Complexity.Medium,
                "Implement feature A by creating the necessary code");

            if (task1Result.IsSuccess)
            {
                project.AddTask(task1Result.Value);
            }

            var task2Result = Domain.Entities.Task.Create(
                Guid.NewGuid(),
                project.Id,
                "TASK-002",
                "Fix bug B",
                "Fix the critical bug in module B",
                Priority.High,
                "Backend",
                1,
                Complexity.Low,
                "Fix the bug in module B by analyzing and correcting the code");

            if (task2Result.IsSuccess)
            {
                project.AddTask(task2Result.Value);
            }

            await StoreSeed.AddProjectAsync(new DaedalusDb(dataSource), project).ConfigureAwait(false);
        }
        catch (DbException)
        {
            // Seeding failed, tests may fail due to missing data
        }
    }

    /// <summary>Stops what this server started; an external host and its database are left as they are.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_setConnectionStringVariable)
        {
            Environment.SetEnvironmentVariable(ConnectionStringEnvironmentVariable, null);
        }

        if (_factory != null)
        {
            await _factory.DisposeAsync().ConfigureAwait(false);
        }

        if (_postgresContainer != null)
        {
            await _postgresContainer.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
