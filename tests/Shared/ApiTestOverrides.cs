using Daedalus.Application.Abstractions;
using Daedalus.Application.Services.CodeAnalysis;
using Daedalus.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Daedalus.Tests.Shared;

/// <summary>
///     What the Playwright.Api suite changes about the Api it tests: stubs for the services that call out, a test
///     authentication scheme, relaxed rate limiters, plain health checks and the workflow engine off. Linked into both
///     hosts of that suite, the in-memory <c>WebApplicationFactory</c> in <c>E2EServerFixture</c> and the
///     <c>Daedalus.Tests.NativeApiHost</c> project that publishes natively, so the two serve the same Api.
/// </summary>
/// <remarks>
///     Kept free of reflection-driven activation, expression trees, dynamic code and type-name matching, because the
///     native host compiles it with Native AOT and full trimming.
/// </remarks>
internal static class ApiTestOverrides
{
    /// <summary>
    ///     The host settings the Api must see while it registers its services, so they are host settings, not app
    ///     configuration. Call it on the web host builder before the Api's services are added.
    /// </summary>
    /// <param name="builder">The web host builder of the host that serves the Api.</param>
    /// <param name="connectionString">The test database.</param>
    public static void ConfigureHost(IWebHostBuilder builder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ConnectionStrings:daedalus"] = connectionString,
            });
        });

        // UseSetting, not ConfigureAppConfiguration, for this one - AddDaedalusAgents reads
        // Workflow.Enabled synchronously while the Api registers services (same "too late" timing
        // ApiWebApplicationFactory's ConnectionStrings:daedalus comment describes), so a
        // ConfigureAppConfiguration value here would not exist yet when the gate is evaluated.
        // The workflow engine stays off for this suite, as it was before the fixture database held the engine's
        // tables: this suite tests the Api's endpoints, not the engine. processes/manufacture.yaml (Task 11)
        // is a real file on Thalos:Workflow's ProcessesRoot and flows into this host's output the same way
        // skills/*.SKILL.md does, so left enabled, ProcessDefinitionSyncHostedService.StartAsync would sync
        // it and the engine's pollers would start against the Api suite's stubbed runtime. The tables exist
        // now, so turning it on is a decision for a later change, not a schema gap.
        builder.UseSetting("Thalos:Workflow:Enabled", "false");

        // A host setting, so the Api sees it while it registers services: AddApplicationDatabase builds the
        // NpgsqlDataSource behind DaedalusDb from it then, before ConfigureAppConfiguration above applies,
        // and the ZeroAlloc.ORM stores and AgentSessionCrashRecovery would open the default database.
        builder.UseSetting("ConnectionStrings:daedalus", connectionString);
    }

    /// <summary>
    ///     Replaces the Api's services that reach outside the test: call it after the Api's own registrations, since
    ///     it removes some of them.
    /// </summary>
    /// <param name="services">The services the Api registered.</param>
    public static void Apply(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Register repositories that are missing from the API project
        services.AddScoped<ITaskRepository, TaskRepository>();

        // Register mock/stub services for external dependencies
        services.AddScoped<IAgentFactory, StubAgentFactory>();

        // Replace services that require HttpClient with stubs
        RemoveService<IPullRequestFactory>(services);
        RemoveService<IRalphLoopOrchestrator>(services);

        services.AddScoped<IPullRequestFactory, StubPullRequestFactory>();
        services.AddScoped<IRalphLoopOrchestrator, StubRalphLoopOrchestrator>();

        // Commands and queries are NOT stubbed here: the 8 commands reachable through
        // IApplicationCommands only touch ITaskRepository/IProjectRepository (both real,
        // against the seeded Postgres database), so the real handlers run and behave
        // exactly like production - including returning "not found" for a missing id.
        // The commands that do call out to an LLM (GeneratePrd, ConvertPrdToTasks) are safe too: they go through
        // IAgentFactory, which is stubbed above.

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

        // Remove and replace health checks to avoid database dependencies: the service that runs them and every
        // configuration of its options, which is where the Api's AddNpgSql registration lives. Matched by type, not by
        // type name, so the native host removes exactly what the in-memory one does.
        RemoveService<HealthCheckService>(services);
        RemoveConfigureOptions<HealthCheckServiceOptions>(services);
        services.AddHealthChecks();

        // Production registers a global 100 req/min limiter (partitioned per user) and a
        // 30 req/min "write-operations" limiter. All E2E tests authenticate as the same
        // TestAuthHandler identity, so they share one partition; a full test run fires far
        // more than 100 requests well within a minute and starts getting 429s that have
        // nothing to do with the behavior under test. Replace the configured RateLimiterOptions
        // with effectively unlimited policies of the same names so [EnableRateLimiting(...)]
        // attributes still resolve, but never actually throttle in tests.
        RemoveConfigureOptions<RateLimiterOptions>(services);
        services.AddRateLimiter(options =>
        {
            options.AddFixedWindowLimiter("llm-operations", limiterOptions =>
            {
                limiterOptions.PermitLimit = int.MaxValue;
                limiterOptions.Window = TimeSpan.FromMilliseconds(1);
            });
            options.AddFixedWindowLimiter("write-operations", limiterOptions =>
            {
                limiterOptions.PermitLimit = int.MaxValue;
                limiterOptions.Window = TimeSpan.FromMilliseconds(1);
            });
        });
    }

    private static void RemoveService<T>(IServiceCollection services)
    {
        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(T));
        if (descriptor != null)
        {
            services.Remove(descriptor);
        }
    }

    /// <summary>
    ///     Removes IConfigureOptions/IPostConfigureOptions registrations for <typeparamref name="TOptions" />.
    ///     Needed for the rate limiter because the Api's AddRateLimiter call already ran by the time this runs, and
    ///     RateLimiterOptions' AddPolicy throws if a policy name is registered twice on the same options instance --
    ///     so the production limiter configuration must be removed outright before re-adding permissive policies.
    /// </summary>
    private static void RemoveConfigureOptions<TOptions>(IServiceCollection services)
        where TOptions : class
    {
        var toRemove = services
            .Where(d => d.ServiceType == typeof(IConfigureOptions<TOptions>)
                        || d.ServiceType == typeof(IPostConfigureOptions<TOptions>))
            .ToList();
        foreach (var descriptor in toRemove)
        {
            services.Remove(descriptor);
        }
    }
}
