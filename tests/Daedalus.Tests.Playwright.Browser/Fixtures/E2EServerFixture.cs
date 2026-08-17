using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Asp.Versioning;
using Daedalus.Agents;
using Daedalus.Api.Services;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Configuration;
using Daedalus.Application.Extensions;
using Daedalus.Application.Services.CodeAnalysis;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Extensions;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Infrastructure.Persistence.Repositories;
using Daedalus.Infrastructure.Services;
using Daedalus.Tests.Playwright.Browser.Fixtures;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Thalos;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Playwright.Browser;

[SetUpFixture]
public class E2EServerFixture
{
    private static PostgreSqlContainer? _postgresContainer;
    private static WebApplication? _app;

    public static Uri? ServerUrl { get; private set; }
    public static string ConnectionString { get; private set; } = string.Empty;
    public static bool IsServerReady { get; private set; }
    public static bool IsBrowserServerReady { get; private set; }
    public static string? BrowserSkipReason { get; private set; }
    public static int Port { get; private set; }

    public static HttpClient CreateClient()
    {
        if (ServerUrl == null)
        {
            throw new InvalidOperationException(
                "Test server is not initialized. Ensure E2EServerFixture has completed setup.");
        }

#pragma warning disable MA0039 // Do not write your own certificate validation method (needed for local testing)
        var handler = new HttpClientHandler();
        handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
#pragma warning restore MA0039

        return new HttpClient(handler, true) { BaseAddress = ServerUrl };
    }

    [OneTimeSetUp]
    public async Task GlobalSetupAsync()
    {
        try
        {
            // 1. Start PostgreSQL container
#pragma warning disable CS0618
            _postgresContainer = new PostgreSqlBuilder()
                .WithImage("pgvector/pgvector:pg16")
                .WithDatabase("daedalus_e2e_test")
                .WithUsername("e2e_user")
                .WithPassword("e2e_password")
                .Build();
#pragma warning restore CS0618

            await _postgresContainer.StartAsync().ConfigureAwait(false);
            ConnectionString = _postgresContainer.GetConnectionString();

            // 2. Pick a random available port
            Port = GetAvailablePort();

            // 3. Build a real Kestrel-based WebApplication
            var testAssemblyDir = Path.GetDirectoryName(typeof(E2EServerFixture).Assembly.Location)!;
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = testAssemblyDir,
                ApplicationName = "Daedalus.Web"
            });
            builder.WebHost.UseUrls($"http://127.0.0.1:{Port}");
            builder.Environment.EnvironmentName = "Development";

            // Force loading static web assets from Daedalus.Web manifest
            // so _framework, index.html, CSS, and Radzen assets are served
            builder.WebHost.UseStaticWebAssets();

            // Database. Singleton consumers (Thalos PostgresAgentSessionStore, AgentSessionCrashRecovery) need
            // IDbContextFactory<T>; register it first so DbContextOptions<T> is the singleton both share, and keep the
            // scoped DbContext's options singleton too (otherwise the factory sees a scoped IDbContextOptionsConfiguration).
            builder.Services.AddPooledDbContextFactory<ApplicationDbContext>(options =>
                options.UseNpgsql(ConnectionString));
            builder.Services.AddDbContext<ApplicationDbContext>(options =>
                options.UseNpgsql(ConnectionString),
                contextLifetime: ServiceLifetime.Scoped, optionsLifetime: ServiceLifetime.Singleton);

            // Core infrastructure (ISystemClock, etc.)
            builder.Services.AddCoreInfrastructureServices();

            // RalphLoop configuration (empty defaults — no real workspace)
            builder.Services.Configure<RalphLoopConfiguration>(_ => { });

            // Application services (handlers, validators, prompt builders, PRD service, etc.)
            builder.Services.AddApplicationServices(builder.Configuration);

            // Code analysis services (repositories, Git operations, etc.)
            builder.Services.AddCodeAnalysisServices(builder.Configuration);

            // Repositories
            builder.Services.AddScoped<ITaskRepository, TaskRepository>();
            builder.Services.AddScoped<IExecutionSessionRepository, ExecutionSessionRepository>();
            builder.Services.AddScoped<IProjectRepository, ProjectRepository>();
            builder.Services.AddScoped<IRepositoryConfigurationRepository, RepositoryConfigurationRepository>();

            // Query services
            builder.Services.AddScoped<ITaskQueryService, TaskQueryService>();
            builder.Services.AddScoped<IExecutionSessionQueryService, ExecutionSessionQueryService>();
            builder.Services.AddScoped<ITaskExecutionQueryService, TaskExecutionQueryService>();
            builder.Services.AddScoped<IProjectQueryService, ProjectQueryService>();

            // Override external services with stubs
            RemoveAndReplace<IRalphAgentFactory, StubRalphAgentFactory>(builder.Services);
            RemoveAndReplace<IPullRequestFactory, StubPullRequestFactory>(builder.Services);
            RemoveAndReplace<IRalphLoopOrchestrator, StubRalphLoopOrchestrator>(builder.Services);
            RemoveAndReplace<ICommandHandlerFactory, StubCommandHandlerFactory>(builder.Services);
            RemoveAndReplace<IQueryHandlerFactory, StubQueryHandlerFactory>(builder.Services);

            // Thalos agents: the real composition root (agent catalog from the API's appsettings.json — linked into this
            // test's output as Daedalus.Api.appsettings.json because Web ships an appsettings.json too; Postgres session
            // store), with the runtime — the only piece that would call Anthropic / start MCP servers — swapped for a
            // scripted stub. The API registers the store as a singleton, so the stub is one too (RemoveAndReplace
            // registers scoped services).
            builder.Configuration.AddJsonFile(Path.Combine(testAssemblyDir, "Daedalus.Api.appsettings.json"), optional: false);
            builder.Services.AddDaedalusAgents(builder.Configuration, builder.Environment);
            foreach (var descriptor in builder.Services.Where(d => d.ServiceType == typeof(IAgentRuntime)).ToList())
            {
                builder.Services.Remove(descriptor);
            }

            builder.Services.AddSingleton<IAgentRuntime, StubAgentRuntime>();

            builder.Services.AddHttpClient();

            // API versioning — matches production config so [ApiVersion] attributes are processed
            builder.Services.AddApiVersioning(options =>
            {
                options.DefaultApiVersion = new ApiVersion(1, 0);
                options.AssumeDefaultVersionWhenUnspecified = true;
                options.ReportApiVersions = true;
                options.ApiVersionReader = ApiVersionReader.Combine(
                    new QueryStringApiVersionReader("api-version"),
                    new HeaderApiVersionReader("x-api-version"),
                    new UrlSegmentApiVersionReader());
            });

            // Controllers — load from the Api assembly
            builder.Services.AddControllers()
                .AddApplicationPart(typeof(Daedalus.Api.Program).Assembly);

            // Rate limiter — required for [EnableRateLimiting] attributes on write endpoints
            builder.Services.AddRateLimiter(options =>
            {
                options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
                    RateLimitPartition.GetNoLimiter("e2e-test"));
                options.AddFixedWindowLimiter("write-operations", limiterOptions =>
                {
                    limiterOptions.PermitLimit = 1000;
                    limiterOptions.Window = TimeSpan.FromMinutes(1);
                });
                options.AddFixedWindowLimiter("llm-operations", limiterOptions =>
                {
                    limiterOptions.PermitLimit = 1000;
                    limiterOptions.Window = TimeSpan.FromMinutes(1);
                });
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            });

            // Exception handling
            builder.Services.AddProblemDetails();

            // Health checks (simple, no NpgSql check — container is already verified)
            builder.Services.AddHealthChecks();

            // CORS — allow everything for tests
            builder.Services.AddCors(options =>
            {
                options.AddPolicy("TestCors", policy =>
                    policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
            });

            // Authentication — TestAuthHandler bypasses real JWT/OIDC
            builder.Services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                    TestAuthHandler.SchemeName, _ => { });
            builder.Services.PostConfigureAll<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                options.DefaultScheme = TestAuthHandler.SchemeName;
            });
            builder.Services.PostConfigureAll<JwtBearerOptions>(options =>
            {
                options.Authority = null;
                options.RequireHttpsMetadata = false;
                options.Configuration = new OpenIdConnectConfiguration();
            });

            // Authorization policies (same as production — TestAuthHandler includes all role claims)
            builder.Services.AddAuthorization(options =>
            {
                options.AddPolicy("TaskRead", policy => policy.RequireAuthenticatedUser());
                options.AddPolicy("TaskManagement", policy => policy.RequireRole("task-manager", "admin"));
                options.AddPolicy("ProjectRead", policy => policy.RequireAuthenticatedUser());
                options.AddPolicy("ProjectManagement", policy => policy.RequireRole("project-manager", "admin"));
                options.AddPolicy("CodeAnalysis", policy => policy.RequireRole("analyst", "admin"));
                options.AddPolicy("CodeAnalysisRead", policy => policy.RequireAuthenticatedUser());
                options.AddPolicy("Admin", policy => policy.RequireRole("admin"));
                options.AddPolicy("AgentUse", policy => policy.RequireAuthenticatedUser());
            });

            // 4. Build and configure middleware
            var app = builder.Build();
            _app = app;

            app.UseCors("TestCors");

            var serverBaseUrl = $"http://127.0.0.1:{Port}";

            // The index.html references blazor.web.js which needs server endpoint metadata.
            // Rewrite to the standalone WASM bootstrap script instead.
            app.Use(async (context, next) =>
            {
                if (string.Equals(context.Request.Path.Value, "/_framework/blazor.web.js",
                        StringComparison.OrdinalIgnoreCase))
                {
                    context.Request.Path = "/_framework/blazor.webassembly.js";
                }

                await next().ConfigureAwait(false);
            });

            // The WASM app boots with Daedalus.Web/wwwroot/appsettings.json (served as a static web asset), which points it
            // at the production API URL and Keycloak. Hand the browser a test-mode configuration instead so it calls this
            // server (Program.cs: TestMode → plain HttpClient at the host base address, AlwaysAuthenticatedStateProvider).
            app.Use(async (context, next) =>
            {
                var path = context.Request.Path.Value;
                if (HttpMethods.IsGet(context.Request.Method)
                    && (string.Equals(path, "/appsettings.json", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(path, "/appsettings.Development.json", StringComparison.OrdinalIgnoreCase)))
                {
                    context.Response.ContentType = "application/json";
                    context.Response.Headers.CacheControl = "no-store";
                    await context.Response.WriteAsync("{\"TestMode\":\"true\"}").ConfigureAwait(false);
                    return;
                }

                await next().ConfigureAwait(false);
            });

            app.UseBlazorFrameworkFiles();
            app.UseStaticFiles();

            app.UseRateLimiter();
            app.UseAuthentication();
            app.UseAuthorization();

            app.MapControllers();
            app.MapHealthChecks("/health");
            app.MapFallbackToFile("index.html");

            // 5. Create database + seed test data
            using (var scope = app.Services.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                // Rag.NET's rag_chunks (Thalos memory index) needs the pgvector extension; EnsureCreatedAsync runs no migration SQL
                await dbContext.Database.ExecuteSqlRawAsync("CREATE EXTENSION IF NOT EXISTS vector;").ConfigureAwait(false);
                await dbContext.Database.EnsureCreatedAsync().ConfigureAwait(false);
                await SeedTestDataAsync(dbContext).ConfigureAwait(false);
            }

            // 6. Start the server
            await app.StartAsync().ConfigureAwait(false);

            ServerUrl = new Uri(serverBaseUrl);
            IsServerReady = true;
            IsBrowserServerReady = true;

            // 7. Verify health endpoint
            using var verifyClient = CreateClient();
            var healthResp = await verifyClient.GetAsync("/health").ConfigureAwait(false);
            healthResp.EnsureSuccessStatusCode();

            await TestContext.Progress.WriteLineAsync(
                    $"E2E Browser Test Server ready at {ServerUrl}")
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await TestContext.Progress.WriteLineAsync($"Failed to start E2E Test Server: {ex.Message}")
                .ConfigureAwait(false);
            IsServerReady = false;
            IsBrowserServerReady = false;
            BrowserSkipReason = $"E2E Test Server failed to start: {ex.Message}";
            // Don't throw — let tests be marked Inconclusive rather than crashing the test host
        }
    }

    private static void RemoveAndReplace<TService, TImplementation>(IServiceCollection services)
        where TService : class
        where TImplementation : class, TService
    {
        // Remove all existing registrations for TService
        var descriptors = services.Where(d => d.ServiceType == typeof(TService)).ToList();
        foreach (var descriptor in descriptors)
        {
            services.Remove(descriptor);
        }

        services.AddScoped<TService, TImplementation>();
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
                Guid.NewGuid(), "Test Project", "A test project for E2E testing");
            if (projectResult.IsFailure)
            {
                return;
            }

            var project = projectResult.Value;

            var task1Result = Domain.Entities.Task.Create(
                Guid.NewGuid(), project.Id, "TASK-001", "Implement feature A",
                "Complete the implementation of feature A",
                Priority.Medium, "Backend", 1,
                Complexity.Medium,
                "Implement feature A by creating the necessary code",
                "Feature A must be working correctly", 10);
            if (task1Result.IsSuccess)
            {
                project.AddTask(task1Result.Value);
            }

            var task2Result = Domain.Entities.Task.Create(
                Guid.NewGuid(), project.Id, "TASK-002", "Fix bug B",
                "Fix the critical bug in module B",
                Priority.High, "Backend", 1,
                Complexity.Low,
                "Fix the bug in module B by analyzing and correcting the code",
                "Bug B must be resolved", 5);
            if (task2Result.IsSuccess)
            {
                project.AddTask(task2Result.Value);
            }

            dbContext.Projects.Add(project);
            await dbContext.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Seed data already exists — safe to ignore
        }
    }

    [OneTimeTearDown]
    public async Task GlobalTeardownAsync()
    {
        try
        {
            if (_app != null)
            {
                await _app.StopAsync().ConfigureAwait(false);
                await _app.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            await TestContext.Progress.WriteLineAsync($"Warning: Server disposal failed: {ex.Message}")
                .ConfigureAwait(false);
        }

        try
        {
            if (_postgresContainer != null)
            {
                await _postgresContainer.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            await TestContext.Progress.WriteLineAsync($"Warning: PostgreSQL container disposal failed: {ex.Message}")
                .ConfigureAwait(false);
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
