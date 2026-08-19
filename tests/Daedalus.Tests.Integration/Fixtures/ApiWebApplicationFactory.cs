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
internal sealed class ApiWebApplicationFactory(string connectionString, IAgentRuntime runtime) : WebApplicationFactory<Daedalus.Api.Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // WebApplicationFactory defaults the content root to the *project* directory (src/Daedalus.Api), but the
        // content this host needs — .mcp.json and skills/**/SKILL.md — is copied to the *output* directory. Point it
        // at the output so the host resolves the same relative paths it resolves in production, where the content root
        // is the publish directory. Without this, Thalos:Skills:Roots ["skills"] resolves under src/Daedalus.Api and
        // registration fails fast, which is exactly what it is designed to do.
        builder.UseContentRoot(AppContext.BaseDirectory);

        // Host settings are visible to Program.cs while it registers services (ConfigureAppConfiguration callbacks run
        // too late for builder.Configuration.GetConnectionString(...) in the minimal-hosting entry point).
        builder.UseSetting("ConnectionStrings:daedalus", connectionString);

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAgentRuntime>();
            services.AddSingleton(runtime);

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
        });
    }
}
