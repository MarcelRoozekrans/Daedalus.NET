using Daedalus.Cli;
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
            CliHostServices.ConfigureServices(services, context.Configuration, context.HostingEnvironment))
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

namespace Daedalus.Cli
{
    /// <summary>
    ///     The CLI host's <c>ConfigureServices</c> body, extracted from <c>Program.cs</c>'s top-level statements so
    ///     it can be called directly from a test with a configuration and hosting environment the test controls,
    ///     rather than only from behind a real <c>HostBuilderContext</c>. A source-text assertion that
    ///     <c>Program.cs</c> merely <i>contains</i> the right call literals would fail for the wrong reasons — a
    ///     reformat or a rename breaks it while the wiring stays correct, and it can pass while the call sits in
    ///     unreachable code — so this extraction is what actually makes the CLI host's composition testable.
    ///     <c>Program.cs</c>'s own lambda is now a single call into this method; that one line is the only
    ///     remaining untested surface.
    /// </summary>
    internal static class CliHostServices
    {
        /// <summary>
        ///     Registers every service the Daedalus CLI host needs. Call order below is load-bearing — see each
        ///     comment for why — and must not be reshuffled without re-reading them.
        /// </summary>
        internal static void ConfigureServices(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
        {
            // Add service defaults (OpenTelemetry, logging)
            services.AddServiceDefaults();

            // Add core infrastructure services (system clock, etc.)
            services.AddCoreInfrastructureServices();

            // Add database (key must match the Aspire database resource name)
            services.AddApplicationDatabase(configuration, "daedalus");

            // Add external service integrations (MCP, workspace context, IFailurePatternDatabase — the last of
            // which AddDaedalusAgents' DaedalusKnowledgeTools requires).
            services.AddExternalServices(configuration);

            // Ollama embedding generator (memory index + Sentinel). Aspire provides ConnectionStrings:ollama when
            // this host runs under the AppHost; without it memories stay index_pending and Sentinel's semantic
            // detectors stay lexical-only, same degradation as the API host.
            var ollamaConnectionString = configuration.GetConnectionString("ollama");
            OllamaSharp.OllamaApiClient? ollama = null;
            if (!string.IsNullOrEmpty(ollamaConnectionString))
            {
                ollama = new OllamaSharp.OllamaApiClient(new Uri(ollamaConnectionString), "nomic-embed-text");
                services.AddSingleton<Microsoft.Extensions.AI.IEmbeddingGenerator<string, Microsoft.Extensions.AI.Embedding<float>>>(ollama);
            }

            // Thalos-based agents: same composition root as the API host (Task 7), needs the DbContext factory
            // (AddApplicationDatabase above) and IFailurePatternDatabase (AddExternalServices above).
            services.AddDaedalusAgents(configuration, environment, ollama);

            // Thalos channels, with the console channel switched on: this is the one host with a TTY to read
            // from, and the whole reason AddDaedalusChannels takes includeConsoleChannel as a parameter rather
            // than always registering it (see the XML doc on AddDaedalusChannels). Must run after
            // AddDaedalusAgents above, which wires the outbox durability layer AddDaedalusChannels deliberately
            // does not wire itself — see the API host's Program.cs for the full reasoning.
            services.AddDaedalusChannels(configuration, includeConsoleChannel: true);

            // Scheduled-run execution: same composition as the API host. Must run after AddDaedalusAgents above,
            // which wires the outbox durability layer this method's dispatcher Replace calls depend on, and which
            // already registers ScheduleReconcilerHostedService — see the XML doc on AddDaedalusScheduling.
            services.AddDaedalusScheduling(configuration);
        }
    }
}
