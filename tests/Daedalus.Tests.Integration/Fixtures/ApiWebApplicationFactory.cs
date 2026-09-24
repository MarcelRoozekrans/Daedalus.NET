using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Thalos;

namespace Daedalus.Tests.Integration.Fixtures;

/// <summary>
///     Boots the real <c>Daedalus.Api</c> <c>Program</c> in-process (TestServer) against the fixture database, with the
///     JWT scheme swapped for <see cref="HeaderTestAuthHandler"/> and Thalos' <see cref="IAgentRuntime"/> replaced by the
///     supplied fake. Everything else — controllers, ProblemDetails, response compression, rate limiting, JSON context,
///     the Postgres session store and the crash-recovery hosted service — is the production wiring.
/// </summary>
/// <remarks>
///     When <paramref name="keycloak"/> is supplied, the production JWT Bearer scheme is left in place and pointed at
///     that fixture's authority instead of being swapped for <see cref="HeaderTestAuthHandler"/> — the only way to
///     exercise the real Keycloak claim shape. When <see langword="null"/> (the default), the host behaves exactly as
///     before.
/// </remarks>
/// <param name="connectionString">The database this host's <c>ConnectionStrings:daedalus</c> is set to.</param>
/// <param name="runtime">Replaces Thalos' registered <see cref="IAgentRuntime"/> for the life of this host.</param>
/// <param name="keycloak">See this type's own remarks.</param>
/// <param name="workflowEnabled">
///     Defaults to <see langword="false"/>, which every existing caller relies on — see the remarks on the
///     <c>Thalos:Workflow:Enabled</c> setting below for why that default holds. Pass <see langword="true"/> only
///     against a <paramref name="connectionString"/> already migrated with Thalos.NET.Workflow.Orm's raw-SQL
///     migrations (see <c>StartRunEndpointTests</c>), never against the shared <c>PostgresFixture</c> database,
///     which is built with EF Core's <c>EnsureCreatedAsync</c> and has none of those tables.
/// </param>
/// <param name="standingInstructionsPath">
///     Task B5. When supplied, overrides <c>Thalos:Workflow:StandingInstructionsPath</c> so
///     <c>StandingInstructionsWriter</c> reads and writes there instead of the default <c>AGENT.md</c> resolved
///     against this factory's content root — <c>src/Daedalus.Api</c>, per this class's own remarks above, a real
///     project directory whose tracked files a test must never touch. <c>AddDaedalusAgents</c> refuses a path
///     outside the content root, so pass a path relative to it from <c>TempDirectory.NewContentRootRelative</c>,
///     which lands under the project's git-ignored <c>obj</c> folder. <see langword="null"/> (the default) leaves
///     the shipped configuration in place, for hosts that never touch the standing-instructions file at all.
/// </param>
/// <param name="squadEnabled">
///     When supplied, overrides <c>Thalos:Squad:Enabled</c>. <see langword="null"/> (the default) keeps the shipped
///     value, which enables the squad.
/// </param>
/// <param name="configureServices">
///     Runs after this factory's own service replacements, for a test that needs one more, such as a faster
///     workflow outbox poll. <see langword="null"/> (the default) adds nothing.
/// </param>
internal sealed class ApiWebApplicationFactory(
    string connectionString, IAgentRuntime runtime, KeycloakFixture? keycloak = null, bool workflowEnabled = false,
    string? standingInstructionsPath = null, bool? squadEnabled = null, Action<IServiceCollection>? configureServices = null)
    : WebApplicationFactory<Daedalus.Api.Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // The content root stays WebApplicationFactory's default — the project directory, src/Daedalus.Api — and must
        // not be repointed at the test output. Api, Console and Web each ship an appsettings.json, so in the output
        // directory whichever copies last wins: pointing the content root there makes the host read a non-deterministic
        // appsettings.json, and the agent list silently comes back empty on whichever machine loses the race. That
        // passed locally and failed in CI. Skills still resolve from here because ResolveSkillRoots falls back to the
        // assembly directory when the content root has no skills folder, which is the same fallback that lets
        // `dotnet run` work.

        // Host settings are visible to Program.cs while it registers services (ConfigureAppConfiguration callbacks run
        // too late for builder.Configuration.GetConnectionString(...) in the minimal-hosting entry point).
        builder.UseSetting("ConnectionStrings:daedalus", connectionString);

        // PostgresFixture builds this database with EF Core's EnsureCreatedAsync, which creates only the EF model
        // (including ApplicationDbContext's own outbox table). It never runs Thalos.NET.Workflow.Orm's raw-SQL
        // migrations, so workflow_run/workflow_run_event/process_definition and the ORM outbox table do not exist
        // here. Left enabled against that database, WorkflowOutboxDispatchService, WorkflowStrandedRunSweepService
        // and ProcessDefinitionSyncHostedService would each tick against those missing tables and fail every cycle
        // with Postgres 42P01 — caught and logged, so silently, for the life of every test that uses this factory.
        // See WorkflowConfig.Enabled's own remarks, and this constructor's own parameter doc, for the one caller
        // that opts into the engine against a database it migrated itself.
        builder.UseSetting("Thalos:Workflow:Enabled", workflowEnabled ? "true" : "false");

        if (standingInstructionsPath is not null)
        {
            builder.UseSetting("Thalos:Workflow:StandingInstructionsPath", standingInstructionsPath);
        }

        if (squadEnabled is { } squad)
        {
            builder.UseSetting("Thalos:Squad:Enabled", squad ? "true" : "false");
        }

        if (keycloak is not null)
        {
            // Same "too late" reasoning as ConnectionStrings above: Program.cs reads these synchronously while
            // registering AddJwtBearer, so they must land before the host builds, not via ConfigureAppConfiguration.
            builder.UseSetting("Authentication:Authority", keycloak.Authority);
            builder.UseSetting("Authentication:Audience", "daedalus-api");
        }

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAgentRuntime>();
            services.AddSingleton(runtime);

            if (keycloak is null)
            {
                services.AddAuthentication(HeaderTestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, HeaderTestAuthHandler>(HeaderTestAuthHandler.SchemeName, _ => { });
                services.PostConfigureAll<AuthenticationOptions>(options =>
                {
                    options.DefaultAuthenticateScheme = HeaderTestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = HeaderTestAuthHandler.SchemeName;
                    options.DefaultScheme = HeaderTestAuthHandler.SchemeName;
                });

                // Never let the JWT handler try OIDC discovery against a Keycloak that is not there.
                services.PostConfigureAll<JwtBearerOptions>(options =>
                {
                    options.Authority = null;
                    options.RequireHttpsMetadata = false;
                    options.Configuration = new OpenIdConnectConfiguration();
                });
            }

            configureServices?.Invoke(services);
        });
    }
}
