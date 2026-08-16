using Daedalus.Web;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using OpenTelemetry.Logs;
using Polly;
using Radzen;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Always register root components — required for standalone WASM (blazor.webassembly.js).
// Root components must be declared here unconditionally so the Blazor renderer has
// an entry point regardless of authentication mode.
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<Microsoft.AspNetCore.Components.Web.HeadOutlet>("head::after");

// Determine test mode: explicitly set via config, or detected when the OIDC
// Authority is missing (e.g. the WASM app is hosted by the E2E test fixture
// which does not serve appsettings.json to the browser runtime).
var testMode = string.Equals(builder.Configuration["TestMode"], "true", StringComparison.OrdinalIgnoreCase)
               || (OperatingSystem.IsBrowser()
                   && string.IsNullOrEmpty(builder.Configuration["Oidc:Authority"]));

// In test mode without explicit ApiBaseUrl, fall back to the host address
// so the WASM app calls back to the same test server.
var apiBaseAddress = builder.Configuration["ApiBaseUrl"];
if (string.IsNullOrEmpty(apiBaseAddress))
{
    apiBaseAddress = testMode
        ? builder.HostEnvironment.BaseAddress
        : "https://localhost:5001";
}

// OpenTelemetry OTLP exporter requires System.Threading.Thread (background export loop),
// which is not available in the browser WASM runtime. Guard with a platform check.
if (!OperatingSystem.IsBrowser())
{
    builder.Services.AddOpenTelemetry()
        .WithLogging();

    builder.Services.AddLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddOpenTelemetry(options =>
        {
            options.IncludeScopes = true;
            options.IncludeFormattedMessage = true;
            options.AddOtlpExporter();
        });
#pragma warning disable CA1416
        // Console logging is only available in debug builds for browser testing
        try
        {
            logging.AddConsole();
        }
        catch
        {
            // Suppress errors if console logging is not available in browser
        }
#pragma warning restore CA1416
    });
}

if (testMode)
{
    // Test mode: plain HttpClient without OIDC token attachment
    builder.Services.AddHttpClient("Daedalus.Api",
        client => client.BaseAddress = new Uri(apiBaseAddress));
}
else
{
    // Production: attach OIDC access token via AuthorizationMessageHandler configured
    // with the API base URL. Using AuthorizationMessageHandler (not BaseAddressAuthorizationMessageHandler)
    // allows cross-origin token attachment when the API is on a different origin than the web app.
    builder.Services.AddHttpClient("Daedalus.Api",
            client => client.BaseAddress = new Uri(apiBaseAddress))
        .AddHttpMessageHandler(sp =>
        {
            var handler = sp.GetRequiredService<AuthorizationMessageHandler>()
                .ConfigureHandler(authorizedUrls: new[] { apiBaseAddress });
            return handler;
        })
        .AddStandardResilienceHandler(options =>
        {
            options.Retry.MaxRetryAttempts = 3;
            options.Retry.BackoffType = DelayBackoffType.Exponential;
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(60);
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
            options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(15);
        });
}

// Provide a scoped HttpClient resolved from the named factory for DI consumers
builder.Services.AddScoped(sp =>
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("Daedalus.Api"));

builder.Services.AddScoped<ApiClient>();
builder.Services.AddScoped<AgentApiClient>();
builder.Services.AddScoped<IProjectApiClient, ProjectApiClient>();
builder.Services.AddRadzenComponents();

if (testMode)
{
    // Test mode: bypass OIDC, use AlwaysAuthenticatedStateProvider
    builder.Services.AddAuthorizationCore();
    builder.Services.AddScoped<AuthenticationStateProvider, AlwaysAuthenticatedStateProvider>();
}
else
{
    // Production: configure OIDC authentication — provider-agnostic, reads from appsettings "Oidc" section
    builder.Services.AddOidcAuthentication(options =>
    {
        builder.Configuration.Bind("Oidc", options.ProviderOptions);
    });
}

await builder.Build().RunAsync();
