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
internal sealed class ApiWebApplicationFactory(string connectionString, IAgentRuntime runtime, KeycloakFixture? keycloak = null)
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
        });
    }
}
