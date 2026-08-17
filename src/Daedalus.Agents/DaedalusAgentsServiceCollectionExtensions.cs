using System.Reflection;
using AI.Sentinel;
using AI.Sentinel.Detection;
using Daedalus.Agents.Memory;
using Daedalus.Agents.Security;
using Daedalus.Agents.Sessions;
using Daedalus.Agents.Tools;
using Daedalus.Application.Abstractions;
using Daedalus.Infrastructure.Agents.Tools;
using Daedalus.Infrastructure.Persistence;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Thalos;
using Thalos.Anthropic;
using Thalos.Mcp;
using Thalos.Memory;
using Thalos.Memory.RagNet;
using Thalos.Sentinel;

namespace Daedalus.Agents;

/// <summary>Composition root for the Thalos-based agent stack. Ralph Loop registrations are untouched (strangler).</summary>
public static class DaedalusAgentsServiceCollectionExtensions
{
    /// <summary>The Thalos tool-source name of <see cref="DaedalusKnowledgeTools"/>; tools appear as <c>daedalus__{tool}</c>.</summary>
    public const string KnowledgeToolSourceName = "daedalus";

    /// <summary>Name of the application database connection string (<c>ConnectionStrings:daedalus</c>), shared with the Rag.NET memory index.</summary>
    public const string DatabaseConnectionName = "daedalus";

    /// <summary>
    ///     Registers Thalos (Anthropic provider, <see cref="PostgresAgentSessionStore"/>, <see cref="DaedalusKnowledgeTools"/>,
    ///     MCP servers from <see cref="DaedalusAgentsOptions.McpConfigPath"/>, the <see cref="DeveloperPolicy"/>, configured
    ///     agents/tool policies, memory (<c>IMemoryService</c> over <see cref="PostgresMemoryStore"/> and the Rag.NET index on
    ///     the application database) and — when enabled — AI.Sentinel) from the <c>Thalos</c> section of
    ///     <paramref name="configuration"/>, plus the <see cref="AgentSessionCrashRecovery"/> hosted service.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Host configuration; <c>Thalos</c>, <c>Thalos:Anthropic</c>, <c>Thalos:Memory</c> and <c>ConnectionStrings:daedalus</c> are read.</param>
    /// <param name="environment">Used to resolve a relative <see cref="DaedalusAgentsOptions.McpConfigPath"/> against the content root.</param>
    /// <param name="embeddingGenerator">
    ///     Optional embedding generator handed to AI.Sentinel. Without it Sentinel's semantic detectors (prompt injection,
    ///     jailbreak, exfiltration, …) return Clean and only the lexical/operational detectors run — Sentinel logs a warning
    ///     per agent pipeline. <c>AddAISentinel</c> runs its configure delegate at registration time (no service provider),
    ///     which is why the host passes the instance instead of it being resolved from DI. The memory index resolves the
    ///     generator from DI (a passed instance is registered with <c>TryAddSingleton</c>, so a host registration wins);
    ///     without one the index is unavailable — remember stores <c>index_pending</c>, recall adds nothing.
    /// </param>
    /// <remarks>
    ///     Requires <c>IDbContextFactory&lt;ApplicationDbContext&gt;</c> (see <c>AddApplicationDatabase</c>) and the
    ///     Infrastructure services behind the knowledge tools (<c>IFailurePatternDatabase</c>) to be registered by the host.
    ///     Nothing here needs <c>ANTHROPIC_API_KEY</c> at registration time; the provider reads it lazily on first use.
    /// </remarks>
    /// <exception cref="InvalidOperationException">An agent id is neither a ULID nor a GUID, a Sentinel action or detector name is unknown.</exception>
    public static IServiceCollection AddDaedalusAgents(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var options = new DaedalusAgentsOptions();
        configuration.GetSection(DaedalusAgentsOptions.SectionName).Bind(options);
        var connectionString = ResolveConnectionString(configuration);

        // The Ralph MCP tool classes double as the implementation behind DaedalusKnowledgeTools (fresh scope per invocation).
        services.AddScoped<DaedalusLearningsTools>();
        services.AddScoped<DaedalusFailurePatternsTools>();

        // Sessions left in Running by a crashed host are reset to Idle before the host serves requests.
        services.AddHostedService<AgentSessionCrashRecovery>();

        services.AddSingleton(options.Memory);
        services.AddSingleton<ILearningsMemory, ThalosLearningsMemory>();

        // The memory index embeds with the DI generator; a host that only hands the instance to this call (for Sentinel) still gets a working index.
        if (embeddingGenerator is not null)
        {
            services.TryAddSingleton(embeddingGenerator);
        }

        services.AddThalos(thalos =>
        {
            ConfigureMemory(thalos, configuration.GetSection(MemoryConfig.SectionName), options.Memory, connectionString);

            thalos.UseAnthropic(configuration)
                .UseSessionStore<PostgresAgentSessionStore>()
                .AddLocalTools(KnowledgeToolSourceName, typeof(DaedalusKnowledgeTools))
                .AddMcpServersFromFile(ResolveMcpConfigPath(options.McpConfigPath, environment))
                .AddPolicy<DeveloperPolicy>();

            foreach (var binding in options.ToolPolicies)
            {
                thalos.RequireToolPolicy(binding.Pattern, binding.Policy);
            }

            foreach (var agent in options.Agents)
            {
                thalos.AddAgent(ToDefinition(agent));
            }

            if (options.Sentinel.Enabled)
            {
                thalos.UseAISentinel(o => ConfigureSentinel(o, options.Sentinel, embeddingGenerator));
            }
        });

        return services;
    }

    /// <summary>
    ///     Memory-only registration for hosts that run Ralph but no Thalos agents (the console worker): the same
    ///     <c>IMemoryService</c>, Postgres store and Rag.NET index as <see cref="AddDaedalusAgents"/>, plus the Ralph port
    ///     <c>ILearningsMemory</c>. No agents, tools, Sentinel or reindex service (the API host runs that one).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Host configuration; <c>Thalos:Memory</c> and <c>ConnectionStrings:daedalus</c> are read.</param>
    /// <remarks>
    ///     Requires <c>IDbContextFactory&lt;ApplicationDbContext&gt;</c>. Register an <c>IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt;</c>
    ///     before this call for a working index; without one memories are stored <c>index_pending</c> until the API host reindexes them.
    /// </remarks>
    public static IServiceCollection AddDaedalusMemory(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new DaedalusAgentsOptions();
        configuration.GetSection(DaedalusAgentsOptions.SectionName).Bind(options);
        var connectionString = ResolveConnectionString(configuration);

        services.AddSingleton(options.Memory);
        services.AddSingleton<ILearningsMemory, ThalosLearningsMemory>();
        services.AddThalos(thalos => ConfigureMemory(thalos, configuration.GetSection(MemoryConfig.SectionName), options.Memory, connectionString));
        return services;
    }

    private static string ResolveConnectionString(IConfiguration configuration) =>
        configuration.GetConnectionString(DatabaseConnectionName) ?? DatabaseSettings.GetDefaultConnectionString();

    private static void ConfigureMemory(ThalosBuilder thalos, IConfigurationSection section, MemoryConfig config, string connectionString)
    {
        thalos.UseMemory(o =>
            {
                section.Bind(o); // Enabled, SharedOwnerId, Recall, Dedupe, ExposeTools straight from Thalos:Memory
                o.Enabled = config.Enabled;
                o.SharedOwnerId ??= config.SharedOwnerId;
            })
            .UseMemoryStore<PostgresMemoryStore>()
            .UseRagNetMemory(o =>
            {
                o.ConnectionString = connectionString; // same database as the app; Rag.NET keeps its own pool
                o.VectorDimensions = config.VectorDimensions;
                o.EnsureSchemaOnStartup = true;
            });
    }

    private static string ResolveMcpConfigPath(string configured, IHostEnvironment environment) =>
        Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured);

    private static AgentDefinition ToDefinition(AgentConfig agent) => new()
    {
        Id = ParseAgentId(agent.Id, agent.Name),
        Name = agent.Name,
        Description = agent.Description,
        Instructions = agent.Instructions,
        Model = agent.Model,
        MaxOutputTokens = agent.MaxOutputTokens,
        Tools = agent.Tools.Count == 0 ? ["*"] : [.. agent.Tools],
        Memory = agent.Memory is null ? null : new AgentMemorySettings { Enabled = agent.Memory.Enabled, TopK = agent.Memory.TopK },
    };

    private static AgentId ParseAgentId(string raw, string name)
    {
        if (AgentId.TryParse(raw, null, out var id))
        {
            return id;
        }

        if (Guid.TryParse(raw, out var guid))
        {
            return new AgentId(guid);
        }

        throw new InvalidOperationException($"Agent '{name}': Id '{raw}' is not a ULID or GUID (Thalos:Agents:*:Id).");
    }

    private static void ConfigureSentinel(SentinelOptions sentinel, SentinelConfig config, IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator)
    {
        sentinel.OnCritical = ParseAction(config.OnCritical, nameof(config.OnCritical));
        sentinel.OnHigh = ParseAction(config.OnHigh, nameof(config.OnHigh));
        sentinel.OnMedium = ParseAction(config.OnMedium, nameof(config.OnMedium));
        sentinel.OnLow = ParseAction(config.OnLow, nameof(config.OnLow));

        // Null keeps Sentinel lexical-only (it warns per pipeline); the API host passes the Ollama generator when configured.
        if (embeddingGenerator is not null)
        {
            sentinel.EmbeddingGenerator = embeddingGenerator;
        }

        foreach (var detectorName in config.DisabledDetectors)
        {
            DisableDetector(sentinel, detectorName);
        }
    }

    private static SentinelAction ParseAction(string value, string key) =>
        Enum.TryParse<SentinelAction>(value, ignoreCase: true, out var action)
            ? action
            : throw new InvalidOperationException(
                $"Thalos:Sentinel:{key} '{value}' is not a Sentinel action ({string.Join(", ", Enum.GetNames<SentinelAction>())}).");

    // Sentinel's per-detector configuration is generic over the detector type (Configure<TDetector>), while configuration can only
    // name detectors; resolve the type from AI.Sentinel's assembly and close the generic once at startup.
    private static void DisableDetector(SentinelOptions sentinel, string detectorName)
    {
        var detectorType = typeof(SentinelOptions).Assembly.GetExportedTypes()
            .FirstOrDefault(t => typeof(IDetector).IsAssignableFrom(t)
                                 && t is { IsAbstract: false, IsInterface: false }
                                 && string.Equals(t.Name, detectorName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Thalos:Sentinel:DisabledDetectors: '{detectorName}' is not an AI.Sentinel detector type name.");

        var configure = typeof(SentinelOptionsConfigureExtensions)
            .GetMethod(nameof(SentinelOptionsConfigureExtensions.Configure), BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("AI.Sentinel's SentinelOptionsConfigureExtensions.Configure<T> was not found.");

        configure.MakeGenericMethod(detectorType).Invoke(null, [sentinel, (Action<DetectorConfiguration>)(c => c.Enabled = false)]);
    }
}
