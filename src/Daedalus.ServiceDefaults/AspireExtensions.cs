using Aspire.Hosting;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using Pgvector.EntityFrameworkCore;

namespace Daedalus.ServiceDefaults;

/// <summary>
///     Service defaults extension for configuring shared services across the Daedalus application.
/// </summary>
public static class AspireExtensions
{
    /// <summary>
    ///     Adds Aspire service defaults to the distributed application builder.
    /// </summary>
    public static IDistributedApplicationBuilder AddServiceDefaults(
        this IDistributedApplicationBuilder builder)
    {
        builder.Services.AddServiceDefaults();
        return builder;
    }

    /// <summary>
    ///     Adds standard service configuration with OpenTelemetry integration.
    /// </summary>
    public static IServiceCollection AddServiceDefaults(
        this IServiceCollection services)
    {
        // Configure OpenTelemetry
        services.AddOpenTelemetry()
            .WithLogging()
            .WithMetrics()
            .WithTracing();

        // Configure structured logging with OpenTelemetry
        services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddOpenTelemetry(options =>
            {
                options.IncludeScopes = true;
                options.IncludeFormattedMessage = true;
                options.AddOtlpExporter();
            });
            logging.AddConsole();
        });

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

        services.AddDbContextPool<ApplicationDbContext>(options =>
            options.UseNpgsql(connectionString, npgsqlOptions => npgsqlOptions.UseVector()));

        // Singleton consumers (e.g. the Thalos IAgentSessionStore) need IDbContextFactory<T>; the pooled factory shares
        // the IDbContextPool<T> registered above (both use TryAdd), so contexts come from one pool either way.
        services.AddPooledDbContextFactory<ApplicationDbContext>(options =>
            options.UseNpgsql(connectionString, npgsqlOptions => npgsqlOptions.UseVector()));

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
