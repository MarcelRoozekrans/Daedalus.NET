using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Publishing;
using Daedalus.AppHost;

var builder = DistributedApplication.CreateBuilder(args);

// Database configuration - read from appsettings
var dbName = builder.Configuration["Database:Name"] ?? "daedalus";

// Create username and password parameters - use environment variables to provide values
// Set them via: $env:PARAMETERS__DB_USERNAME="postgres"; $env:PARAMETERS__DB_PASSWORD="postgres"
var dbUserParam = builder.AddParameter("db-username");
var dbPasswordParam = builder.AddParameter("db-password", true);
var anthropicApiKey = builder.AddParameter("anthropic-api-key", true);

// Telegram bot token for the `api` project's Thalos channel (Task 7). Unlike db-password/anthropic-api-key
// above, this parameter is OPTIONAL: Thalos:Channels:Telegram:BotToken blank means Telegram is never
// registered at all (see AddDaedalusChannels.IsTelegramConfigured), so the AppHost must still start when no
// token is configured. EmptyStringParameterDefault (bottom of this file) supplies that empty fallback while
// still letting the value be overridden the same way db-password/anthropic-api-key are: AppHost user-secrets
// (`Parameters:telegram-bot-token`) or `$env:PARAMETERS__TELEGRAM_BOT_TOKEN`. The token itself must never be
// written into this repository - see Daedalus.Api/appsettings.json's Thalos:Channels:Telegram comment for the
// full secret-handling story, including the single-instance operational trap.
var telegramBotToken = builder.AddParameter("telegram-bot-token", new EmptyStringParameterDefault(), secret: true);

// Resolve project paths relative to solution root (src/) and repo root
var solutionRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
var repoRoot = Path.GetFullPath(Path.Combine(solutionRoot, ".."));
var migrationsPath = Path.Combine(solutionRoot, "Daedalus.Migrations/Daedalus.Migrations.csproj");
var consolePath = Path.Combine(solutionRoot, "Daedalus.Console/Daedalus.Console.csproj");
var apiPath = Path.Combine(solutionRoot, "Daedalus.Api/Daedalus.Api.csproj");
var webPath = Path.Combine(solutionRoot, "Daedalus.Web/Daedalus.Web.csproj");
var keycloakRealmPath = Path.Combine(repoRoot, "keycloak-realm.json");

// Configure PostgreSQL database with Aspire orchestration (Docker container)
// pgvector/pgvector:pg16 (Debian-based, not alpine): the AddSemanticEmbeddings migration runs CREATE EXTENSION vector,
// which the stock postgres:16 image cannot satisfy. Same image as docker-compose*.yml and the Testcontainers fixture.
var database = builder.AddPostgres("postgres", dbUserParam, dbPasswordParam)
    .WithImage("pgvector/pgvector")
    .WithImageTag("pg16")
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

// Configure Ollama for embedding generation (semantic search)
var ollama = builder.AddOllama("ollama")
    .WithDataVolume()
    .AddModel("nomic-embed-text");

// Add database migrations - runs after Keycloak is ready
var migrations = builder.AddProject("migrations", migrationsPath)
    .WithReference(database)
    .WithReference(keycloak)
    .WaitFor(database)
    .WaitFor(keycloak);

// Add the Daedalus console application (depends on migrations and Keycloak).
// WaitForCompletion, not WaitFor: `migrations` is a one-shot job, and WaitFor releases as soon as it is
// *running* — so the worker used to start against a database whose schema had not been applied (and even
// when the job exited 1). WaitForCompletion(0) blocks until it exits successfully.
builder.AddProject("console", consolePath)
    .WithReference(database)
    .WithReference(keycloak)
    .WithReference(migrations)
    .WithReference(ollama)
    .WithEnvironment("ANTHROPIC_API_KEY", anthropicApiKey)
    .WaitForCompletion(migrations)
    .WaitFor(keycloak);

// Add the API service (depends on migrations and Keycloak)
// Override Authentication:Authority with the host-reachable Keycloak URL
// (appsettings.json uses Docker hostname "keycloak" which doesn't resolve on the host)
var api = builder.AddProject("api", apiPath)
    .WithReference(database)
    .WithReference(keycloak)
    .WithReference(migrations)
    .WithReference(ollama)
    .WithHttpEndpoint(port: 5010, targetPort: 5010, name: "api-http", isProxied: false)
    .WithExternalHttpEndpoints()
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("Authentication__Authority", "http://localhost:8082/realms/daedalus")
    .WithEnvironment("ANTHROPIC_API_KEY", anthropicApiKey)
    .WithEnvironment("Thalos__Channels__Telegram__BotToken", telegramBotToken)
    .WaitForCompletion(migrations)
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

namespace Daedalus.AppHost
{
    /// <summary>
    ///     Empty-string fallback for the <c>telegram-bot-token</c> parameter above: <c>AddParameter(name, secret:
    ///     true)</c> alone has no default and throws <c>MissingParameterValueException</c> at resolution time when
    ///     unconfigured, which is right for db-password/anthropic-api-key (every host needs those) but wrong here —
    ///     Telegram is opt-in per host, so a developer who never touches Telegram must still be able to start the
    ///     AppHost. Resolution still checks <c>Parameters:telegram-bot-token</c> configuration first (AppHost
    ///     user-secrets or <c>PARAMETERS__TELEGRAM_BOT_TOKEN</c>) via the same <c>ParameterDefault</c>-based
    ///     <c>AddParameter</c> overload the framework itself uses for db-password/anthropic-api-key; this type only
    ///     supplies what happens when that lookup finds nothing.
    /// </summary>
    file sealed class EmptyStringParameterDefault : ParameterDefault
    {
        public override string GetDefaultValue() => string.Empty;

        public override void WriteToManifest(ManifestPublishingContext context) =>
            context.Writer.WriteString("value", string.Empty);
    }
}
