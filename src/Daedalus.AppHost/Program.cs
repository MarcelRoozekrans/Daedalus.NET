var builder = DistributedApplication.CreateBuilder(args);

// Database configuration - read from appsettings
var dbName = builder.Configuration["Database:Name"] ?? "daedalus";

// Create username and password parameters - use environment variables to provide values
// Set them via: $env:PARAMETERS__DB_USERNAME="postgres"; $env:PARAMETERS__DB_PASSWORD="postgres"
var dbUserParam = builder.AddParameter("db-username");
var dbPasswordParam = builder.AddParameter("db-password", true);

// Resolve project paths relative to solution root (src/) and repo root
var solutionRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
var repoRoot = Path.GetFullPath(Path.Combine(solutionRoot, ".."));
var migrationsPath = Path.Combine(solutionRoot, "Daedalus.Migrations/Daedalus.Migrations.csproj");
var consolePath = Path.Combine(solutionRoot, "Daedalus.Console/Daedalus.Console.csproj");
var apiPath = Path.Combine(solutionRoot, "Daedalus.Api/Daedalus.Api.csproj");
var webPath = Path.Combine(solutionRoot, "Daedalus.Web/Daedalus.Web.csproj");
var keycloakRealmPath = Path.Combine(repoRoot, "keycloak-realm.json");

// Configure PostgreSQL database with Aspire orchestration (Docker container)
// Using standard image (not alpine) for better locale support
var database = builder.AddPostgres("postgres", dbUserParam, dbPasswordParam)
    .WithImageTag("16")
    .WithDataVolume()
    .AddDatabase(dbName);

// Configure Keycloak for identity and access management
// Auto-imports the daedalus-realm from keycloak-realm.json
var keycloak = builder.AddKeycloak("daedalus-realm", port: 8082)
    .WithImageTag("26.1")
    .WithRealmImport(keycloakRealmPath)
    // Workaround: Aspire's default health check targets management port 9000 over HTTPS,
    // but Keycloak's self-signed cert isn't trusted by the health check client.
    // Use legacy observability to expose /health on main HTTP port 8080,
    // then replace the built-in management health check with one on the HTTP endpoint (dotnet/aspire#7787).
    .WithEnvironment("KC_LEGACY_OBSERVABILITY_INTERFACE", "true")
    .WithEnvironment("KC_HEALTH_ENABLED", "true")
    .WaitFor(database);

// Remove the original management-based health check annotation and add one for the HTTP endpoint
var managementHealthCheck = keycloak.Resource.Annotations
    .OfType<Aspire.Hosting.ApplicationModel.HealthCheckAnnotation>()
    .ToList();
foreach (var hc in managementHealthCheck)
{
    keycloak.Resource.Annotations.Remove(hc);
}

keycloak.WithHttpHealthCheck("/health/ready");

// Add database migrations - runs after Keycloak is ready
var migrations = builder.AddProject("migrations", migrationsPath)
    .WithReference(database)
    .WithReference(keycloak)
    .WaitFor(database)
    .WaitFor(keycloak);

// Add the Daedalus console application (depends on migrations and Keycloak)
builder.AddProject("console", consolePath)
    .WithReference(database)
    .WithReference(keycloak)
    .WithReference(migrations)
    .WaitFor(migrations)
    .WaitFor(keycloak);

// Add the API service (depends on migrations and Keycloak)
// Override Authentication:Authority with the host-reachable Keycloak URL
// (appsettings.json uses Docker hostname "keycloak" which doesn't resolve on the host)
var api = builder.AddProject("api", apiPath)
    .WithReference(database)
    .WithReference(keycloak)
    .WithReference(migrations)
    .WithHttpEndpoint(port: 5010, targetPort: 5010, name: "api-http", isProxied: false)
    .WithExternalHttpEndpoints()
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("Authentication__Authority", "http://localhost:8082/realms/daedalus")
    .WaitFor(migrations)
    .WaitFor(keycloak);

// Add the Blazor WASM frontend with Keycloak reference
builder.AddProject("web", webPath)
    .WithReference(keycloak)
    .WithHttpEndpoint(port: 5290, targetPort: 5290, name: "web-http", isProxied: false)
    .WithExternalHttpEndpoints()
    .WithEnvironment("ApiBaseUrl", api.GetEndpoint("api-http"))
    .WaitFor(api)
    .WaitFor(keycloak);

await builder.Build().RunAsync();
