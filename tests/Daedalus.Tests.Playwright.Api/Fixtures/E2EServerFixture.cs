using System.Net;
using System.Net.Sockets;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Services.CodeAnalysis;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Playwright.Api.Fixtures;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
// Alias to resolve ambiguity between Daedalus.Api.Program and Daedalus.Console.Program
using ApiProgram = Daedalus.Api.Program;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Playwright.Api;

/// <summary>
///     NUnit SetUpFixture that manages the lifecycle of the test server and database container.
///     This runs once before all tests and once after all tests in the namespace.
///     Uses WebApplicationFactory with in-memory TestServer; tests access via HttpClient.
/// </summary>
[SetUpFixture]
public class E2EServerFixture
{
    private static PostgreSqlContainer? _postgresContainer;
    private static WebApplicationFactory<ApiProgram>? _factory;

    /// <summary>
    ///     The base URL where the test server is listening (in-memory TestServer).
    /// </summary>
    public static Uri? ServerUrl { get; private set; }

    /// <summary>
    ///     The connection string for the test database.
    /// </summary>
    public static string ConnectionString { get; private set; } = string.Empty;

    /// <summary>
    ///     Indicates whether the server is ready for tests.
    /// </summary>
    public static bool IsServerReady { get; private set; }

    /// <summary>
    ///     The port (kept for backward compatibility, not used by TestServer).
    /// </summary>
    public static int Port { get; private set; }

    /// <summary>
    ///     Creates an HttpClient configured to communicate with the in-memory test server.
    /// </summary>
    public static HttpClient CreateClient()
    {
        if (_factory == null)
        {
            throw new InvalidOperationException(
                "Test server is not initialized. Ensure E2EServerFixture has completed setup.");
        }

        return _factory.CreateClient();
    }

    [OneTimeSetUp]
    public async Task GlobalSetupAsync()
    {
        try
        {
            // Start PostgreSQL container
#pragma warning disable CS0618 // Using PostgreSqlBuilder - obsolete warning for parameterless constructor
            _postgresContainer = new PostgreSqlBuilder()
                .WithDatabase("daedalus_e2e_test")
                .WithUsername("e2e_user")
                .WithPassword("e2e_password")
                .Build();
#pragma warning restore CS0618

            await _postgresContainer.StartAsync().ConfigureAwait(false);
            ConnectionString = _postgresContainer.GetConnectionString();

            // Set connection string as environment variable BEFORE creating WebApplicationFactory
            Environment.SetEnvironmentVariable("ConnectionStrings__daedalus", ConnectionString);

            // Find an available port (kept for ServerUrl compatibility)
            Port = GetAvailablePort();
            ServerUrl = new Uri($"http://localhost:{Port}");

            // Create and configure the WebApplicationFactory (uses in-memory TestServer)
            _factory = new WebApplicationFactory<ApiProgram>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Development");

                    builder.ConfigureAppConfiguration((context, config) =>
                    {
                        config.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["ConnectionStrings:daedalus"] = ConnectionString
                        });
                    });

                    builder.ConfigureServices(services =>
                    {
                        // Remove ALL existing DbContext registrations to avoid conflicts
                        RemoveService<ApplicationDbContext>(services);
                        RemoveService<DbContextOptions<ApplicationDbContext>>(services);
                        RemoveService<IDbContextFactory<ApplicationDbContext>>(services);

                        // Remove all pooling-related services by partial type name match
                        RemoveServicesByType(services, "DbContextPool");
                        RemoveServicesByType(services, "IDbContextPool");
                        RemoveServicesByType(services, "IScopedDbContextLease");
                        RemoveServicesByType(services, "PooledDbContextFactory");

                        // Add DbContext with test container connection string (non-pooled for testing)
                        services.AddDbContext<ApplicationDbContext>(options =>
                        {
                            options.UseNpgsql(ConnectionString);
                        });

                        // Register repositories that are missing from the API project
                        services.AddScoped<ITaskRepository, TaskRepository>();
                        services.AddScoped<IExecutionSessionRepository, ExecutionSessionRepository>();

                        // Register mock/stub services for external dependencies
                        services.AddScoped<ILlmService, StubLlmService>();

                        // Replace services that require HttpClient with stubs
                        RemoveService<IMcpAgentSelector>(services);
                        RemoveService<IContext7DocumentationInjector>(services);
                        RemoveService<IPullRequestFactory>(services);
                        RemoveService<IRalphLoopOrchestrator>(services);

                        services.AddScoped<IMcpAgentSelector, StubMcpAgentSelector>();
                        services.AddScoped<IContext7DocumentationInjector, StubContext7DocumentationInjector>();
                        services.AddScoped<IPullRequestFactory, StubPullRequestFactory>();
                        services.AddScoped<IRalphLoopOrchestrator, StubRalphLoopOrchestrator>();

                        // Register stub factories for CQRS handler resolution
                        services.AddScoped<ICommandHandlerFactory, StubCommandHandlerFactory>();
                        services.AddScoped<IQueryHandlerFactory, StubQueryHandlerFactory>();

                        // Add HttpClient for any services that still need it
                        services.AddHttpClient();

                        // Replace real JWT Bearer authentication with a test scheme
                        services.AddAuthentication(TestAuthHandler.SchemeName)
                            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                                TestAuthHandler.SchemeName, _ => { });
                        services.PostConfigureAll<AuthenticationOptions>(options =>
                        {
                            options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                            options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                            options.DefaultScheme = TestAuthHandler.SchemeName;
                        });

                        // Disable JwtBearer OIDC discovery
                        services.PostConfigureAll<JwtBearerOptions>(options =>
                        {
                            options.Authority = null;
                            options.RequireHttpsMetadata = false;
                            options.Configuration = new OpenIdConnectConfiguration();
                        });

                        // Remove and replace health checks to avoid database dependencies
                        RemoveServicesByType(services, "HealthCheck");
                        services.AddHealthChecks();
                    });
                });

            // Create database schema from model
            using var scope = _factory.Services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await dbContext.Database.EnsureCreatedAsync().ConfigureAwait(false);

            // Seed some initial data for testing
            await SeedTestDataAsync(dbContext).ConfigureAwait(false);

            // Verify the server is responding via in-memory TestServer
            using var client = _factory.CreateClient();
            var response = await client.GetAsync("/health").ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            IsServerReady = true;
            await TestContext.Progress.WriteLineAsync($"E2E Test Server started at {ServerUrl}").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await TestContext.Progress.WriteLineAsync($"Failed to start E2E Test Server: {ex.Message}")
                .ConfigureAwait(false);
            IsServerReady = false;
            throw;
        }
    }

    private static void RemoveService<T>(IServiceCollection services)
    {
        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(T));
        if (descriptor != null)
        {
            services.Remove(descriptor);
        }
    }

    private static void RemoveServicesByType(IServiceCollection services, string typeNamePart)
    {
        var toRemove = services
            .Where(d => d.ServiceType.Name.Contains(typeNamePart, StringComparison.Ordinal))
            .ToList();
        foreach (var descriptor in toRemove)
        {
            services.Remove(descriptor);
        }
    }

    private static async Task SeedTestDataAsync(ApplicationDbContext dbContext)
    {
        try
        {
            if (await dbContext.Projects.AnyAsync().ConfigureAwait(false))
            {
                return;
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
                "Implement feature A by creating the necessary code",
                "Feature A must be working correctly",
                10);

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
                "Fix the bug in module B by analyzing and correcting the code",
                "Bug B must be resolved",
                5);

            if (task2Result.IsSuccess)
            {
                project.AddTask(task2Result.Value);
            }

            dbContext.Projects.Add(project);
            await dbContext.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Seeding failed, tests may fail due to missing data
        }
    }

    [OneTimeTearDown]
    public async Task GlobalTeardownAsync()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__daedalus", null);

        if (_factory != null)
        {
            await _factory.DisposeAsync().ConfigureAwait(false);
        }

        if (_postgresContainer != null)
        {
            await _postgresContainer.DisposeAsync().ConfigureAwait(false);
        }

        await TestContext.Progress.WriteLineAsync("E2E Test Server stopped").ConfigureAwait(false);
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
