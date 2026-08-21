using Daedalus.Agents;
using Daedalus.Agents.Channels;
using Daedalus.Application.Abstractions;
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

            // Add database (key must match the Aspire database resource name)
            services.AddApplicationDatabase(context.Configuration, "daedalus");

            // Add external service integrations (MCP, workspace context, IFailurePatternDatabase — the last of
            // which AddDaedalusAgents' DaedalusKnowledgeTools requires).
            services.AddExternalServices(context.Configuration);

            // Ollama embedding generator (memory index + Sentinel). Aspire provides ConnectionStrings:ollama when
            // this host runs under the AppHost; without it memories stay index_pending and Sentinel's semantic
            // detectors stay lexical-only, same degradation as the API host.
            var ollamaConnectionString = context.Configuration.GetConnectionString("ollama");
            OllamaSharp.OllamaApiClient? ollama = null;
            if (!string.IsNullOrEmpty(ollamaConnectionString))
            {
                ollama = new OllamaSharp.OllamaApiClient(new Uri(ollamaConnectionString), "nomic-embed-text");
                services.AddSingleton<Microsoft.Extensions.AI.IEmbeddingGenerator<string, Microsoft.Extensions.AI.Embedding<float>>>(ollama);
            }

            // Thalos-based agents: same composition root as the API host (Task 7), needs the DbContext factory
            // (AddApplicationDatabase above) and IFailurePatternDatabase (AddExternalServices above).
            services.AddDaedalusAgents(context.Configuration, context.HostingEnvironment, ollama);

            // Thalos channels, with the console channel switched on: this is the one host with a TTY to read
            // from, and the whole reason AddDaedalusChannels takes includeConsoleChannel as a parameter rather
            // than always registering it (see the XML doc on AddDaedalusChannels). Must run after
            // AddDaedalusAgents above, which wires the outbox durability layer AddDaedalusChannels deliberately
            // does not wire itself — see the API host's Program.cs for the full reasoning.
            services.AddDaedalusChannels(context.Configuration, includeConsoleChannel: true);
        })
        .Build();

    var appLogger = builder.Services.GetRequiredService<ILogger<Program>>();
    appLogger.LogInformation("Starting Daedalus CLI — the console channel pump runs as a hosted service");
    appLogger.LogInformation("Ensure database migrations have been run via 'Daedalus.Migrations' before starting");
    await builder.RunAsync();
}
catch (Exception ex)
{
    await Console.Error.WriteLineAsync($"Application terminated unexpectedly: {ex}");
    Environment.Exit(1);
}
