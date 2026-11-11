using Daedalus.Api;
using Daedalus.Tests.Shared;

// The Api as Daedalus.Api's Program.cs composes it through ApiApplication, with the Playwright.Api suite's overrides applied in the two places
// E2EServerFixture applies them to its WebApplicationFactory: host settings before the Api registers its services, and
// the service replacements after. The environment is Development, as in that fixture. The content root is the binary's
// own directory, where the build copies the Api's appsettings, skills, roles and process definitions.
const string ConnectionStringVariable = "E2E_CONNECTION_STRING";

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    EnvironmentName = Environments.Development,
    ContentRootPath = AppContext.BaseDirectory,
});

var connectionString = builder.Configuration[ConnectionStringVariable];
if (string.IsNullOrEmpty(connectionString))
{
    throw new InvalidOperationException(
        $"{ConnectionStringVariable} must name the test database, migrated before this host starts.");
}

ApiTestOverrides.ConfigureHost(builder.WebHost, connectionString);
var api = ApiApplication.AddServices(builder);
ApiTestOverrides.Apply(builder.Services);

var app = builder.Build();
api.UsePipeline(app);

await app.RunAsync();
