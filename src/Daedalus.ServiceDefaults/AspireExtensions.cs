using Aspire.Hosting;
using Daedalus.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Daedalus.ServiceDefaults;

/// <summary>
///     Service defaults extension for configuring shared services across the Daedalus application.
/// </summary>
public static class AspireExtensions
{
    /// <summary>
    ///     The configuration key naming the OTLP collector endpoint, the standard OpenTelemetry environment variable. Aspire
    ///     sets it for every project it runs.
    /// </summary>
    public const string OtlpEndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary>The health endpoint. Its requests are not traced: a probe every few seconds would bury the real traces.</summary>
    public const string HealthPath = "/health";

    /// <summary>The liveness endpoint, filtered like <see cref="HealthPath"/>.</summary>
    public const string AlivePath = "/alive";

    /// <summary>
    ///     The activity sources traced: Thalos agent turns and stores, the workflow engine, the mediator, and every
    ///     Daedalus source, and Minos, the decision client's own spans. No Npgsql source: the outbox poller would
    ///     drown the traces (decision D3).
    /// </summary>
    private static readonly string[] TracedSources = ["thalos", "thalos.workflow", "ZeroAlloc.Mediator", "daedalus.*", "Minos"];

    /// <summary>The meters collected, plus the .NET runtime's built-in <c>System.Runtime</c> meter.</summary>
    private static readonly string[] Meters = ["thalos", "thalos.workflow", "daedalus.*", "Minos", "System.Runtime"];

    /// <summary>
    ///     Whether ASP.NET Core instrumentation traces this request: every request except those to <see cref="HealthPath"/>
    ///     or <see cref="AlivePath"/> and their sub-paths, compared case-insensitively.
    /// </summary>
    public static bool IsTracedRequest(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var path = context.Request.Path;
        return !path.StartsWithSegments(HealthPath, StringComparison.OrdinalIgnoreCase)
            && !path.StartsWithSegments(AlivePath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Adds Aspire service defaults to the distributed application builder.
    /// </summary>
    public static IDistributedApplicationBuilder AddServiceDefaults(
        this IDistributedApplicationBuilder builder)
    {
        builder.Services.AddServiceDefaults(builder.Configuration);
        return builder;
    }

    /// <summary>
    ///     Adds standard service configuration with OpenTelemetry integration. Traces and metrics cover Thalos, the
    ///     workflow engine, ZeroAlloc.Mediator, Daedalus, ASP.NET Core and HttpClient. All three signals go over OTLP only
    ///     when <see cref="OtlpEndpointKey"/> is set.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Read for <see cref="OtlpEndpointKey"/>.</param>
    public static IServiceCollection AddServiceDefaults(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // Export over OTLP only where a collector is configured, as Aspire's own service defaults do. Without one the
        // exporter still targets the default endpoint, and on every shutdown the batch processor spends about 4 seconds
        // on a final export to a collector that is not there.
        var exportOtlp = !string.IsNullOrWhiteSpace(configuration[OtlpEndpointKey]);

        var telemetry = services.AddOpenTelemetry()
            .WithLogging()
            .WithMetrics(metrics => metrics
                .AddMeter(Meters)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation())
            .WithTracing(tracing => tracing
                .AddSource(TracedSources)
                .AddAspNetCoreInstrumentation(options => options.Filter = IsTracedRequest)
                .AddHttpClientInstrumentation()
                // An outgoing call's path can hold a secret, so only its origin is exported. The OTLP exporter is ordered
                // last by its own pipeline weight, so it always sees the cut span. A hand-added exporter has weight 0 like
                // this processor and runs in registration order: register it after AddServiceDefaults.
                .AddProcessor(new HttpClientOriginOnlyProcessor())
                // The console's hub connection carries its bearer token in the query string, which the server span
                // exports as url.query. The instrumentation's own redaction can be turned off by a setting, so the
                // token's value is redacted here too, before any exporter sees it.
                .AddProcessor(new ServerQueryRedactionProcessor()));

        // One exporter for all three signals, configured from the standard OTEL_* settings Aspire sets.
        if (exportOtlp)
        {
            telemetry.UseOtlpExporter();
            services.TryAddSingleton(TelemetryShutdownFlushOptions.Default);
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, TelemetryShutdownFlush>());
        }

        services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddOpenTelemetry(options =>
            {
                options.IncludeScopes = true;
                options.IncludeFormattedMessage = true;
            });
            logging.AddConsole();
        });

        // The hosting diagnostics' "Request starting" line carries the query string, and the console's hub connection
        // carries its bearer token there. Filtered in code, after every configuration source, so no setting can turn it
        // back on; see HostingDiagnosticsLogFilter for why a plain AddFilter rule is not enough.
        services.PostConfigure<LoggerFilterOptions>(HostingDiagnosticsLogFilter.Apply);

        return services;
    }

    /// <summary>
    ///     Adds database context with connection string resolution.
    /// </summary>
    public static IServiceCollection AddApplicationDatabase(
        this IServiceCollection services,
        IConfiguration configuration,
        string connectionStringKey = "DefaultConnection",
        string? fallbackConnectionString = null)
    {
        fallbackConnectionString ??= DatabaseSettings.GetDefaultConnectionString();
        var connectionString = configuration.GetConnectionString(connectionStringKey) ?? fallbackConnectionString;

        services.AddDbContextPool<ApplicationDbContext>(options => options.UseNpgsql(connectionString));

        // Singleton consumers (e.g. the Thalos IAgentSessionStore) need IDbContextFactory<T>; the pooled factory shares
        // the IDbContextPool<T> registered above (both use TryAdd), so contexts come from one pool either way.
        services.AddPooledDbContextFactory<ApplicationDbContext>(options => options.UseNpgsql(connectionString));

        return services;
    }

    /// <summary>
    ///     Adds minimal logging for CLI/console applications (migrations tool, etc).
    /// </summary>
    public static IServiceCollection AddConsoleLogging(
        this IServiceCollection services,
        LogLevel minimumLevel = LogLevel.Information)
    {
        services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddConsole();
            logging.SetMinimumLevel(minimumLevel);
        });

        return services;
    }
}
