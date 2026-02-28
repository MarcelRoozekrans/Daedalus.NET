using Daedalus.Application.Abstractions;
using Daedalus.Application.Configuration;
using Daedalus.Application.Extensions;
using Daedalus.Application.Services;
using Daedalus.Console;
using Daedalus.Infrastructure.Extensions;
using Daedalus.Infrastructure.Persistence;
using Daedalus.ServiceDefaults;

try
{
    var builder = Host.CreateDefaultBuilder(args)
        .ConfigureServices((context, services) =>
        {
            // Add service defaults (OpenTelemetry, logging)
            services.AddServiceDefaults();

            // Add core infrastructure services (system clock, etc.)
            services.AddCoreInfrastructureServices();

            // Register RalphLoop configuration using options pattern
            services.Configure<RalphLoopConfiguration>(options =>
                    // IL2026: ConfigurationBinder.Bind requires runtime reflection which is not AOT-safe
                    // but this code only runs in non-trimmed Console host, not in AOT-compiled API
#pragma warning disable IL2026
                    context.Configuration.GetSection(RalphLoopConfiguration.SectionName).Bind(options)
#pragma warning restore IL2026
            );

            // Add database (key must match the Aspire database resource name)
            services.AddApplicationDatabase(context.Configuration, "daedalus");

            // Repositories
            services.AddScoped<ITaskRepository, TaskRepository>();
            services.AddScoped<IExecutionSessionRepository, ExecutionSessionRepository>();
            services.AddScoped<IProjectRepository, ProjectRepository>();

            // Add application layer services (command/query handlers, prompt builders)
            services.AddApplicationServices(context.Configuration);

            // Add external service integrations (MCP agents, git, workspace)
            services.AddExternalServices(context.Configuration);

            // Add code analysis services (git change applier, git repository manager, etc.)
            services.AddCodeAnalysisServices(context.Configuration);

            // Add Agent Framework services (Claude via IRalphAgentFactory, MCP tools)
            services.AddAgentFrameworkServices(context.Configuration);

            // Register Ralph loop middleware pipeline
            services.AddRalphLoopMiddleware(context.Configuration);

            // Services
            services.AddScoped<ITaskAssignmentService, TaskAssignmentService>();

            // Worker
            services.AddHostedService<RalphLoopWorker>();
        })
        .Build();

    // Note: Database migrations are now run by a separate Daedalus.Migrations binary
    // This keeps the Console app optimized for AOT compilation

    var appLogger = builder.Services.GetRequiredService<ILogger<Program>>();
    appLogger.LogInformation("Starting Ralph Loop Console Application");
    appLogger.LogInformation("Ensure database migrations have been run via 'Daedalus.Migrations' before starting");
    await builder.RunAsync();
}
catch (Exception ex)
{
    await Console.Error.WriteLineAsync($"Application terminated unexpectedly: {ex}");
    Environment.Exit(1);
}
