using Daedalus.Application.Abstractions;
using Daedalus.Application.Configuration;
using Daedalus.Application.Extensions;
using Daedalus.Application.Services;
using Daedalus.Console;
using Daedalus.Infrastructure.Extensions;
using Daedalus.Infrastructure.Llm;
using Daedalus.Infrastructure.Persistence;
using Daedalus.ServiceDefaults;
using GitHub.Copilot.SDK;

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

            // Add external service integrations (MCP agents, Context7 documentation)
            services.AddExternalServices(context.Configuration);

            // Add LLM services (Claude, factory)
            services.AddLlmServices(context.Configuration);

            // Register Ralph loop middleware pipeline
            services.AddRalphLoopMiddleware(context.Configuration);

            // Services
            services.AddScoped<ITaskAssignmentService, TaskAssignmentService>();

            // LLM Service - GitHub Copilot SDK (primary provider, also registered as ILlmService)
            services.AddScoped<CopilotLlmService>(sp =>
                new CopilotLlmService(
                    sp.GetRequiredService<CopilotClient>(),
                    context.Configuration["Copilot:Model"] ?? "gpt-4",
                    sp.GetRequiredService<ILogger<CopilotLlmService>>()));
            services.AddScoped<ILlmService>(sp => sp.GetRequiredService<CopilotLlmService>());

            // Register CopilotClient as a hosted service to manage lifecycle
            services.AddSingleton(sp =>
            {
                var config = sp.GetRequiredService<IConfiguration>();
                var logger = sp.GetRequiredService<ILogger<CopilotClient>>();

                var cliPath = config["Copilot:CliPath"];
                var logLevel = config["Copilot:LogLevel"] ?? "warning";

                var options = new CopilotClientOptions { AutoStart = true, AutoRestart = true, LogLevel = logLevel };

                if (!string.IsNullOrEmpty(cliPath))
                {
                    options.CliPath = cliPath;
                }

                logger.LogInformation("Initializing Copilot client with model from configuration");
                return new CopilotClient(options);
            });

            // Register CopilotClient lifecycle manager
            services.AddHostedService<CopilotClientLifecycleService>();

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
