using System.Globalization;
using System.Reflection;
using AI.Sentinel;
using AI.Sentinel.Detection;
using Daedalus.Agents.Memory;
using Daedalus.Agents.Security;
using Daedalus.Agents.Sessions;
using Daedalus.Agents.Tools;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Configuration;
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
    /// <exception cref="InvalidOperationException">
    ///     An agent id is neither a ULID nor a GUID, a Sentinel action or detector name is unknown, a <c>Thalos:Memory</c>
    ///     value is out of range, or memory is already registered on this collection (see <see cref="AddDaedalusMemory"/>).
    /// </exception>
    public static IServiceCollection AddDaedalusAgents(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        ThrowIfMemoryAlreadyRegistered(services, nameof(AddDaedalusAgents));

        var options = new DaedalusAgentsOptions();
        configuration.GetSection(DaedalusAgentsOptions.SectionName).Bind(options);
        var connectionString = ResolveConnectionString(configuration);

        // The Ralph MCP failure-patterns tool class doubles as the implementation behind DaedalusKnowledgeTools (fresh
        // scope per invocation). search_learnings is not wrapped: agents recall learnings through the memory__* tools.
        services.AddScoped<DaedalusFailurePatternsTools>();

        // Sessions left in Running by a crashed host are reset to Idle before the host serves requests.
        services.AddHostedService<AgentSessionCrashRecovery>();

        ValidateMemoryConfig(options.Memory);
        services.TryAddSingleton(options.Memory);
        services.TryAddSingleton(options.Memory.RalphRecall);
        services.TryAddSingleton<ILearningsMemory, ThalosLearningsMemory>();

        // The memory index embeds with the DI generator; a host that only hands the instance to this call (for Sentinel) still gets a working index.
        if (embeddingGenerator is not null)
        {
            services.TryAddSingleton(embeddingGenerator);
        }

        services.AddThalos(thalos =>
        {
            // This host owns the Rag.NET schema: the API is the only host that creates rag_chunks (see AddDaedalusMemory).
            ConfigureMemory(thalos, configuration.GetSection(MemoryConfig.SectionName), options.Memory, connectionString, ensureSchema: true);

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

        // Only this host sweeps: memories written index_pending (Ollama down, console host, migrated learnings) get embedded
        // here. Registered after AddThalos so hosted-service start order matches the dependency order — the Rag.NET schema
        // initializer runs first, and the sweeper's StartupDelay covers the rest.
        if (options.Memory.Enabled && options.Memory.Reindex.Enabled)
        {
            services.AddHostedService<ReindexPendingMemoriesHostedService>();
        }

        return services;
    }

    /// <summary>
    ///     Memory-only registration for hosts that run Ralph but <b>no</b> Thalos agents — the console worker. Registers the
    ///     same <c>IMemoryService</c>, Postgres store and Rag.NET index as <see cref="AddDaedalusAgents"/>, plus the Ralph
    ///     port <c>ILearningsMemory</c>. No agents, tools, Sentinel or reindex service (the API host runs that one).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Host configuration; <c>Thalos:Memory</c> and <c>ConnectionStrings:daedalus</c> are read.</param>
    /// <remarks>
    ///     <para>
    ///         Mutually exclusive with <see cref="AddDaedalusAgents"/>, and checked: calling both throws. The Daedalus
    ///         registrations are <c>TryAdd</c>-based, but <c>UseRagNetMemory</c> is last-call-wins, so a later
    ///         <see cref="AddDaedalusMemory"/> would flip <c>EnsureSchemaOnStartup</c> back to <c>false</c> on the API host
    ///         — nobody would create <c>rag_chunks</c>, every memory would stay <c>index_pending</c>, and nothing would fail
    ///         loudly.
    ///     </para>
    ///     <para>
    ///         <b>The API host owns the Rag.NET schema.</b> This call sets <c>EnsureSchemaOnStartup = false</c>, because the
    ///         AppHost starts the API and the console concurrently against one database and two racing
    ///         <c>CREATE EXTENSION</c>/<c>CREATE TABLE</c>/<c>CREATE INDEX</c> sweeps can fail on the pg catalog and take a
    ///         host down. Until the API has created <c>rag_chunks</c>, this host degrades along the designed path: memories
    ///         are stored <c>index_pending</c> and the API's reindex sweeper embeds them afterwards.
    ///     </para>
    ///     <para>
    ///         Requires <c>IDbContextFactory&lt;ApplicationDbContext&gt;</c>. Register an <c>IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt;</c>
    ///         before this call for a working index; without one memories are likewise stored <c>index_pending</c>.
    ///     </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     A <c>Thalos:Memory</c> value is out of range, or memory is already registered on this collection — this method
    ///     and <see cref="AddDaedalusAgents"/> are mutually exclusive.
    /// </exception>
    public static IServiceCollection AddDaedalusMemory(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ThrowIfMemoryAlreadyRegistered(services, nameof(AddDaedalusMemory));

        var options = new DaedalusAgentsOptions();
        configuration.GetSection(DaedalusAgentsOptions.SectionName).Bind(options);
        var connectionString = ResolveConnectionString(configuration);

        ValidateMemoryConfig(options.Memory);
        services.TryAddSingleton(options.Memory);
        services.TryAddSingleton(options.Memory.RalphRecall);
        services.TryAddSingleton<ILearningsMemory, ThalosLearningsMemory>();
        services.AddThalos(thalos => ConfigureMemory(
            thalos, configuration.GetSection(MemoryConfig.SectionName), options.Memory, connectionString, ensureSchema: false));
        return services;
    }

    private static string ResolveConnectionString(IConfiguration configuration) =>
        configuration.GetConnectionString(DatabaseConnectionName) ?? DatabaseSettings.GetDefaultConnectionString();

    /// <summary>
    ///     Fails fast on the Daedalus-only <c>Thalos:Memory</c> keys (Thalos validates its own <c>MemoryOptions</c> on start).
    ///     A bad value here would otherwise surface much later as a rejected memory or a mis-sized vector column.
    /// </summary>
    private static void ValidateMemoryConfig(MemoryConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.SharedOwnerId))
        {
            throw new InvalidOperationException($"{MemoryConfig.SectionName}:SharedOwnerId must not be blank.");
        }

        if (config.VectorDimensions <= 0)
        {
            throw new InvalidOperationException(
                $"{MemoryConfig.SectionName}:VectorDimensions must be greater than 0, but was {config.VectorDimensions}.");
        }

        if (config.RalphRecall.TopK < RalphRecallConfiguration.MinTopK)
        {
            throw new InvalidOperationException(
                $"{RalphRecallConfiguration.SectionName}:TopK must be at least {RalphRecallConfiguration.MinTopK}, but was {config.RalphRecall.TopK}.");
        }

        if (config.RalphRecall.MinScore is < 0 or > 1 || double.IsNaN(config.RalphRecall.MinScore))
        {
            throw new InvalidOperationException(
                $"{RalphRecallConfiguration.SectionName}:MinScore must be in [0, 1], but was {config.RalphRecall.MinScore.ToString(CultureInfo.InvariantCulture)}.");
        }

        ValidateInterval(config.Reindex.StartupDelay, nameof(ReindexConfig.StartupDelay));
        ValidateInterval(config.Reindex.RetryInterval, nameof(ReindexConfig.RetryInterval));
        ValidateInterval(config.Reindex.SweepInterval, nameof(ReindexConfig.SweepInterval));

        static void ValidateInterval(TimeSpan value, string key)
        {
            if (value <= TimeSpan.Zero)
            {
                throw new InvalidOperationException(
                    $"{MemoryConfig.SectionName}:Reindex:{key} must be greater than zero, but was {value.ToString(null, CultureInfo.InvariantCulture)}.");
            }
        }
    }

    /// <summary>
    ///     Refuses a second memory registration. <c>UseRagNetMemory</c> is last-call-wins, so calling
    ///     <see cref="AddDaedalusAgents"/> and <see cref="AddDaedalusMemory"/> on one host would silently leave
    ///     <c>EnsureSchemaOnStartup</c> at whatever the later call passed — on the API host that means nobody creates
    ///     <c>rag_chunks</c> and every memory stays <c>index_pending</c> with nothing failing. Fail loudly instead.
    /// </summary>
    private static void ThrowIfMemoryAlreadyRegistered(IServiceCollection services, string method)
    {
        if (services.Any(d => d.ServiceType == typeof(MemoryConfig)))
        {
            throw new InvalidOperationException(
                $"{method} was called on a service collection that already registers Daedalus memory. " +
                $"{nameof(AddDaedalusAgents)} and {nameof(AddDaedalusMemory)} are mutually exclusive: the API host calls " +
                $"{nameof(AddDaedalusAgents)} (which owns the Rag.NET schema), every other host calls {nameof(AddDaedalusMemory)}.");
        }
    }

    /// <summary>
    ///     Registers the memory triple on the Thalos builder: <c>Thalos:Memory</c> → <c>MemoryOptions</c>, the Postgres store
    ///     and the Rag.NET index on the application database. <paramref name="ensureSchema"/> decides whether this host
    ///     creates the Rag.NET schema on start — exactly one host may, see the remarks on <see cref="AddDaedalusMemory"/>.
    /// </summary>
    private static void ConfigureMemory(
        ThalosBuilder thalos,
        IConfigurationSection section,
        MemoryConfig config,
        string connectionString,
        bool ensureSchema)
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
                o.EnsureSchemaOnStartup = ensureSchema;
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
