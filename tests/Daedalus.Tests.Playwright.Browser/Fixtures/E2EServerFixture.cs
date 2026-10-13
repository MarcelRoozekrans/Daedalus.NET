using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Asp.Versioning;
using Daedalus.Agents;
using Daedalus.Api.Hubs;
using Daedalus.Api.Services;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Configuration;
using Daedalus.Application.DTOs.WorkflowRuns;
using Daedalus.Application.Extensions;
using Daedalus.Application.Services.CodeAnalysis;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Extensions;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Infrastructure.Persistence.Repositories;
using Daedalus.Infrastructure.Services;
using Daedalus.Migrations;
using Daedalus.Tests.Playwright.Browser.Fixtures;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Npgsql;
using Thalos;
using Thalos.Memory;
using Thalos.Workflow;
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

    /// <summary>Set only when the environment is genuinely absent (no Docker daemon) — the one case that may skip.</summary>
    public static string? BrowserSkipReason { get; private set; }

    /// <summary>Set when the server tried to start and failed; such a run must go red, never "skipped".</summary>
    public static string? StartupFailure { get; private set; }

    public static int Port { get; private set; }

    /// <summary>The seeded runs and the store they were written through; set during set-up, before the server starts.</summary>
    public static RunSeeds Runs { get; private set; } = null!;

    /// <summary>
    ///     The engine's background work, by implementation type: the hosted services that dispatch, sweep or sync workflow
    ///     state. Matched by full name because several are internal to Daedalus.Agents.
    /// </summary>
    private static readonly string[] WorkflowBackgroundWork =
    [
        "ZeroAlloc.Outbox.OutboxWorkerService",
        "Thalos.Sandbox.SandboxReconcileService",
        "Daedalus.Agents.Workflow.ProcessDefinitionSyncHostedService",
        "Daedalus.Agents.Workflow.NodeUsageBackfill",
        "Daedalus.Agents.Workflow.WorkflowOutboxDispatchService",
        "Daedalus.Agents.Workflow.WorkflowStrandedRunSweepService",
        "Daedalus.Agents.Workflow.RunWorkspaceSweepService",
    ];

    /// <summary>
    ///     Every hosted service the host runs once the background work is gone: the seven AddDaedalusAgents leaves, which
    ///     run today with the engine off and dispatch, sweep and sync no workflow state; the console's broadcaster; and
    ///     the two ASP.NET Core adds itself, for data protection and for AddHealthChecks.
    /// </summary>
    private static readonly string[] HostedServicesKept =
    [
        "Daedalus.Agents.Sessions.AgentSessionCrashRecovery",
        "Daedalus.Agents.Scheduling.ScheduleReconcilerHostedService",
        "Thalos.Memory.RagNet.RagNetMemorySchemaInitializer",
        "Thalos.Skills.SkillSyncService",
        "Thalos.Skills.Charters.CharterSyncService",
        "Daedalus.Agents.Memory.ReindexPendingMemoriesHostedService",
        "Daedalus.Agents.Security.CSharpWriteBindingCheck",
        "Daedalus.Api.Hubs.WorkflowRunChangeBroadcaster",
        "Microsoft.AspNetCore.DataProtection.Internal.DataProtectionHostedService",
        "Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckPublisherHostedService",
    ];

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
        // Step 1 — start PostgreSQL. A missing Docker daemon is the one condition that legitimately skips this suite, so it
        // is caught on its own; every later step is allowed to fail the run.
#pragma warning disable CS0618
        _postgresContainer = new PostgreSqlBuilder()
            .WithImage("pgvector/pgvector:pg16")
            .WithDatabase("daedalus_e2e_test")
            .WithUsername("e2e_user")
            .WithPassword("e2e_password")
            .Build();
#pragma warning restore CS0618

        try
        {
            await _postgresContainer.StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (IsDockerUnavailable(ex))
        {
            BrowserSkipReason = $"Docker is not available for the E2E test server ({ex.GetType().Name}: {ex.Message})";
            await TestContext.Progress.WriteLineAsync(BrowserSkipReason).ConfigureAwait(false);
            return;
        }

        try
        {
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

            // Application services (handlers, validators, prompt builders, PRD service, etc.)
            builder.Services.AddApplicationServices(builder.Configuration);

            // Code analysis services (repositories, Git operations, etc.)
            builder.Services.AddCodeAnalysisServices(builder.Configuration);

            // Repositories
            builder.Services.AddScoped<ITaskRepository, TaskRepository>();
            builder.Services.AddScoped<IProjectRepository, ProjectRepository>();
            builder.Services.AddScoped<IRepositoryConfigurationRepository, RepositoryConfigurationRepository>();

            // Query services
            builder.Services.AddScoped<ITaskQueryService, TaskQueryService>();
            builder.Services.AddScoped<TaskManufactureService>();
            builder.Services.AddScoped<ITaskExecutionQueryService, TaskExecutionQueryService>();
            builder.Services.AddScoped<IProjectQueryService, ProjectQueryService>();

            // Override external services with stubs. Commands and queries are NOT stubbed here: the
            // 8 commands reachable through IApplicationCommands only touch ITaskRepository/
            // IProjectRepository (both real, against the seeded Postgres container below), so the real
            // handlers run and behave exactly like production - including returning "not found" for a
            // missing id. The commands that do call out to an LLM (GeneratePrd,
            // ConvertPrdToTasks) are safe too: they go through IAgentFactory, which
            // is stubbed here.
            RemoveAndReplace<IAgentFactory, StubAgentFactory>(builder.Services);
            RemoveAndReplace<IPullRequestFactory, StubPullRequestFactory>(builder.Services);
            RemoveAndReplace<IRalphLoopOrchestrator, StubRalphLoopOrchestrator>(builder.Services);

            // Thalos agents: the real composition root (agent catalog from the API's appsettings.json — linked into this
            // test's output as Daedalus.Api.appsettings.json because Web ships an appsettings.json too; Postgres session
            // store), with the runtime — the only piece that would call Anthropic / start MCP servers — swapped for a
            // scripted stub. The API registers the store as a singleton, so the stub is one too (RemoveAndReplace
            // registers scoped services).
            builder.Configuration.AddJsonFile(Path.Combine(testAssemblyDir, "Daedalus.Api.appsettings.json"), optional: false);

            // AddDaedalusAgents resolves the memory connection string from configuration (Rag.NET keeps its own pool and
            // creates rag_chunks on start); without this it would fall back to the developer's local Postgres.
            builder.Configuration.AddInMemoryCollection(
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["ConnectionStrings:daedalus"] = ConnectionString,
                    // The console's scenarios read runs through the real workflow store, so the engine is on and its
                    // tables are migrated below. Its background work is removed right after AddDaedalusAgents, so
                    // nothing dispatches, sweeps or syncs: a seeded run stays exactly where its seed left it.
                    ["Thalos:Workflow:Enabled"] = "true",
                });
            builder.Services.AddDaedalusAgents(builder.Configuration, builder.Environment);
            RemoveWorkflowBackgroundWork(builder.Services);
            foreach (var descriptor in builder.Services.Where(d => d.ServiceType == typeof(IAgentRuntime)).ToList())
            {
                builder.Services.Remove(descriptor);
            }

            builder.Services.AddSingleton<IAgentRuntime, StubAgentRuntime>();

            // No Ollama here, so the Rag.NET index would probe as unavailable; a stub keeps memories out of index_pending.
            foreach (var descriptor in builder.Services.Where(d => d.ServiceType == typeof(IMemoryIndex)).ToList())
            {
                builder.Services.Remove(descriptor);
            }

            builder.Services.AddSingleton<IMemoryIndex, StubMemoryIndex>();

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

            // The console's live channel, as Program.cs wires it on a host with the engine on: the run hub's messages
            // serialize through their source-generated context, and one broadcaster instance is both the store's
            // observer and the hosted loop that sends. Registered after the background work is removed, so it stays.
            builder.Services.AddSignalR().AddJsonProtocol(options =>
                options.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, WorkflowRunsJsonContext.Default));
            builder.Services.AddSingleton<WorkflowRunChangeBroadcaster>();
            builder.Services.AddSingleton<IWorkflowRunObserver>(sp => sp.GetRequiredService<WorkflowRunChangeBroadcaster>());
            builder.Services.AddHostedService(sp => sp.GetRequiredService<WorkflowRunChangeBroadcaster>());

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
                options.AddPolicy("WorkflowResume", policy => policy.RequireRole("developer", "admin"));
                options.AddPolicy("AgentUse", policy => policy.RequireAuthenticatedUser());
            });

            ExpectOnlyKeptHostedServices(builder.Services);

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
            app.MapHub<WorkflowRunsHub>(WorkflowRunsHub.Path);
            app.MapHealthChecks("/health");
            app.MapFallbackToFile("index.html");

            // 5. Create database + seed test data
            using (var scope = app.Services.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                // Rag.NET's rag_chunks (Thalos memory index) needs the pgvector extension; EnsureCreatedAsync runs no migration SQL
                await dbContext.Database.ExecuteSqlRawAsync("CREATE EXTENSION IF NOT EXISTS vector;").ConfigureAwait(false);
                await dbContext.Database.EnsureCreatedAsync().ConfigureAwait(false);
                await MigrateWorkflowEngineAsync(ConnectionString).ConfigureAwait(false);
                await SeedTestDataAsync(dbContext).ConfigureAwait(false);
                await SeedMemoriesAsync(dbContext).ConfigureAwait(false);
            }

            var runs = new RunSeeds(app.Services.GetRequiredService<IWorkflowStore>(), app.Services.GetRequiredService<IServiceScopeFactory>());
            await runs.SeedAsync(CancellationToken.None).ConfigureAwait(false);
            Runs = runs;

            // 6. Start the server
            await app.StartAsync().ConfigureAwait(false);

            ServerUrl = new Uri(serverBaseUrl);
            IsServerReady = true;
            IsBrowserServerReady = true;

            // 7. Verify the endpoints the suite depends on. /health covers the host; /api/agent-memories covers the Thalos
            //    memory wiring (store + index + Rag.NET connection string), which otherwise degrades silently.
            using var verifyClient = CreateClient();
            var healthResp = await verifyClient.GetAsync("/health").ConfigureAwait(false);
            healthResp.EnsureSuccessStatusCode();
            var memoriesResp = await verifyClient.GetAsync("/api/agent-memories").ConfigureAwait(false);
            if (!memoriesResp.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"GET /api/agent-memories answered {(int)memoriesResp.StatusCode}: {await memoriesResp.Content.ReadAsStringAsync().ConfigureAwait(false)}");
            }

            await TestContext.Progress.WriteLineAsync(
                    $"E2E Browser Test Server ready at {ServerUrl}")
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Docker is up but the host itself is broken: record it for the per-fixture guard and fail the run. Swallowing
            // this is what let a broken memory connection string masquerade as "skipped" for twelve tasks.
            IsServerReady = false;
            IsBrowserServerReady = false;
            StartupFailure = $"E2E Test Server failed to start: {ex.GetType().Name}: {ex.Message}";
            await TestContext.Progress.WriteLineAsync(StartupFailure).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    ///     Whether <paramref name="exception"/> means "no Docker daemon here" (a developer with Docker Desktop stopped)
    ///     rather than "the E2E host is broken". Testcontainers reports a missing endpoint as an
    ///     <see cref="ArgumentException"/> naming Docker, and daemon-side refusals as Docker.DotNet exceptions.
    /// </summary>
    /// <remarks>
    ///     Deliberately narrow. A bare <see cref="SocketException"/>/<see cref="HttpRequestException"/> is <em>not</em>
    ///     accepted: a genuine Postgres or host failure carries one too (an unreachable database surfaces as
    ///     <c>SocketException</c> under <c>NpgsqlException</c>), and swallowing those as "skipped" is precisely the failure
    ///     mode this guard exists to prevent. An unclassified environment problem therefore fails the run — the safe
    ///     direction, since a skipped browser suite is invisible in a green build.
    /// </remarks>
    private static bool IsDockerUnavailable(Exception exception)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            var type = ex.GetType();
            if (type.Namespace?.StartsWith("Docker.DotNet", StringComparison.Ordinal) == true
                || type.Name is "DockerApiException" or "DockerUnavailableException"
                || (ex is ArgumentException && ex.Message.Contains("Docker", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Removes each <see cref="WorkflowBackgroundWork"/> hosted service, and fails the fixture unless each was
    ///     registered exactly once. A renamed service, or a factory that starts returning another type, then breaks set-up
    ///     instead of leaving a dispatcher running against the seeded runs. A factory registration is matched by its
    ///     factory's declared return type, which for every one of these is the concrete service.
    /// </summary>
    private static void RemoveWorkflowBackgroundWork(IServiceCollection services)
    {
        var found = services
            .Where(d => IsHostedService(d)
                        && HostedServiceType(d)?.FullName is { } name
                        && WorkflowBackgroundWork.Contains(name, StringComparer.Ordinal))
            .ToList();

        foreach (var descriptor in found)
        {
            services.Remove(descriptor);
        }

        var counts = WorkflowBackgroundWork
            .Select(name => (Name: name, Count: found.Count(d => string.Equals(HostedServiceType(d)?.FullName, name, StringComparison.Ordinal))))
            .Where(c => c.Count != 1)
            .ToList();
        if (counts.Count > 0)
        {
            throw new InvalidOperationException(
                "Workflow background work not removed exactly once: "
                + string.Join(", ", counts.Select(c => $"{c.Name} x{c.Count}")));
        }
    }

    /// <summary>
    ///     Fails the fixture unless the hosted services left are exactly <see cref="HostedServicesKept"/>, so a service
    ///     added to the composition root later cannot start dispatching against the seeded runs unnoticed.
    /// </summary>
    private static void ExpectOnlyKeptHostedServices(IServiceCollection services)
    {
        var remaining = services
            .Where(IsHostedService)
            .Select(d => HostedServiceType(d)?.FullName ?? "(unknown)")
            .Order(StringComparer.Ordinal)
            .ToList();
        var expected = HostedServicesKept.Order(StringComparer.Ordinal).ToList();
        if (!remaining.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "The hosted services left are not the ones kept: " + string.Join(", ", remaining));
        }
    }

    /// <summary>
    ///     Whether <paramref name="descriptor"/> registers a hosted service. A keyed descriptor is never one the host
    ///     starts, and reading its implementation members throws, so it is ruled out first.
    /// </summary>
    private static bool IsHostedService(ServiceDescriptor descriptor) =>
        !descriptor.IsKeyedService && descriptor.ServiceType == typeof(IHostedService);

    /// <summary>The type a hosted service registration runs: its implementation type, instance or factory return type.</summary>
    private static Type? HostedServiceType(ServiceDescriptor descriptor) =>
        descriptor.ImplementationType
        ?? descriptor.ImplementationInstance?.GetType()
        ?? descriptor.ImplementationFactory?.Method.ReturnType;

    /// <summary>
    ///     The workflow engine's raw-SQL schema, through the same step Daedalus.Migrations runs at deploy,
    ///     <see cref="WorkflowEngineMigrator.ApplyAsync"/>, so the sources and their order cannot drift from it. A refusal
    ///     fails set-up with the migrator's reason.
    /// </summary>
    private static async Task MigrateWorkflowEngineAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        var outcome = await WorkflowEngineMigrator.ApplyAsync(connection).ConfigureAwait(false);
        if (outcome.IsFailure)
        {
            throw new InvalidOperationException($"The workflow engine migration was refused: {outcome.Error}");
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
                "Implement feature A by creating the necessary code");
            if (task1Result.IsSuccess)
            {
                project.AddTask(task1Result.Value);
            }

            var task2Result = Domain.Entities.Task.Create(
                Guid.NewGuid(), project.Id, "TASK-002", "Fix bug B",
                "Fix the critical bug in module B",
                Priority.High, "Backend", 1,
                Complexity.Low,
                "Fix the bug in module B by analyzing and correcting the code");
            if (task2Result.IsSuccess)
            {
                project.AddTask(task2Result.Value);
            }

            // TASK-003 carries the seeded Running run (RunSeeds), so the Tasks grid links its badge to that run. It is a
            // task of its own so TASK-001 stays Pending with Manufacture on offer for the board's scenarios.
            var task3Result = Domain.Entities.Task.Create(
                Guid.NewGuid(), project.Id, RunSeeds.LinkedTaskId, RunSeeds.LinkedTaskTitle,
                "A task whose manufacture run the console follows",
                Priority.Low, "Backend", 1,
                Complexity.Low,
                "Follow the run from the board");
            if (task3Result.IsSuccess)
            {
                project.AddTask(task3Result.Value);
            }

            dbContext.Projects.Add(project);

            // TASK-001 stands in for an old task-loop task. Task.Create no longer takes the loop settings, so set them
            // as EF does when it reads an old row. TASK-002 stays a new task with none.
            if (task1Result.IsSuccess)
            {
                dbContext.Entry(task1Result.Value).Property(t => t.MaxIterations).CurrentValue = 10;
            }

            await dbContext.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Seed data already exists — safe to ignore
        }
    }

    /// <summary>Text of the memory the forget scenario archives; nothing else may depend on it.</summary>
    public const string DisposableMemoryText = "This memory exists so the forget scenario has something of its own to archive.";

    /// <summary>Text of the shared-owner memory (rendered with the "shared" marker).</summary>
    public const string SharedMemoryText = "Playwright locators on the PRD page use data-testid.";

    /// <summary>Text of the memory pinned to another agent; visible to no caller of the memories endpoints.</summary>
    public const string PinnedMemoryText = "Only the agent this memory is pinned to may see it.";

    /// <summary>
    ///     Memories the Agent page can show: one owned by the E2E user, one owned by the shared owner (host-written project
    ///     knowledge), one disposable row for the forget scenario, and one pinned to an unrelated agent that must stay
    ///     invisible. Seeded through the aggregate, so the rows are exactly what
    ///     <see cref="Daedalus.Agents.Memory.PostgresMemoryStore"/> would have written.
    /// </summary>
    private static async Task SeedMemoriesAsync(ApplicationDbContext dbContext)
    {
        if (await dbContext.AgentMemories.AnyAsync().ConfigureAwait(false))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var seeds = new[]
        {
            AgentMemory.Create(
                Guid.NewGuid(), TestAuthHandler.UserId, null, "fact", "The E2E user prefers xUnit over NUnit.",
                ["testing"], "seed", 0.7, now, indexPending: false),
            AgentMemory.Create(
                Guid.NewGuid(), StubAgentRuntime.SharedOwnerId, null, "learning", SharedMemoryText,
                ["codeconvention", "low"], "migration", 0.3, now.AddDays(-3), indexPending: false),
            AgentMemory.Create(
                Guid.NewGuid(), TestAuthHandler.UserId, null, "note", DisposableMemoryText,
                ["testing"], "seed", 0.4, now.AddMinutes(-5), indexPending: false),
            AgentMemory.Create(
                Guid.NewGuid(), TestAuthHandler.UserId, Guid.NewGuid(), "note", PinnedMemoryText,
                ["testing"], "seed", 0.4, now.AddMinutes(-10), indexPending: false),
        };

        // Throw rather than return: a seed the domain rejects means the memory scenario would assert against rows that
        // are not there, and a silently unseeded fixture is exactly the failure mode this suite was fixed for.
        foreach (var seed in seeds)
        {
            if (seed.IsFailure)
            {
                throw new InvalidOperationException($"Seeding agent memories failed: {seed.Error}");
            }

            dbContext.AgentMemories.Add(seed.Value);
        }

        await dbContext.SaveChangesAsync().ConfigureAwait(false);
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
