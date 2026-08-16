using System.Reflection;
using AI.Sentinel;
using AI.Sentinel.Detection;
using Daedalus.Agents.Security;
using Daedalus.Agents.Sessions;
using Daedalus.Agents.Tools;
using Daedalus.Infrastructure.Agents.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Thalos;
using Thalos.Anthropic;
using Thalos.Mcp;
using Thalos.Sentinel;

namespace Daedalus.Agents;

/// <summary>Composition root for the Thalos-based agent stack. Ralph Loop registrations are untouched (strangler).</summary>
public static class DaedalusAgentsServiceCollectionExtensions
{
    /// <summary>The Thalos tool-source name of <see cref="DaedalusKnowledgeTools"/>; tools appear as <c>daedalus__{tool}</c>.</summary>
    public const string KnowledgeToolSourceName = "daedalus";

    /// <summary>
    ///     Registers Thalos (Anthropic provider, <see cref="PostgresAgentSessionStore"/>, <see cref="DaedalusKnowledgeTools"/>,
    ///     MCP servers from <see cref="DaedalusAgentsOptions.McpConfigPath"/>, the <see cref="DeveloperPolicy"/>, configured
    ///     agents/tool policies and — when enabled — AI.Sentinel) from the <c>Thalos</c> section of <paramref name="configuration"/>,
    ///     plus the <see cref="AgentSessionCrashRecovery"/> hosted service.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Host configuration; <c>Thalos</c> and <c>Thalos:Anthropic</c> are read.</param>
    /// <param name="environment">Used to resolve a relative <see cref="DaedalusAgentsOptions.McpConfigPath"/> against the content root.</param>
    /// <param name="embeddingGenerator">
    ///     Optional embedding generator handed to AI.Sentinel. Without it Sentinel's semantic detectors (prompt injection,
    ///     jailbreak, exfiltration, …) return Clean and only the lexical/operational detectors run — Sentinel logs a warning
    ///     per agent pipeline. <c>AddAISentinel</c> runs its configure delegate at registration time (no service provider),
    ///     which is why the host passes the instance instead of it being resolved from DI.
    /// </param>
    /// <remarks>
    ///     Requires <c>IDbContextFactory&lt;ApplicationDbContext&gt;</c> (see <c>AddApplicationDatabase</c>) and the
    ///     Infrastructure services behind the knowledge tools (<c>ILearningsRepository</c>, <c>IEmbeddingService</c>,
    ///     <c>IFailurePatternDatabase</c>) to be registered by the host. Nothing here needs <c>ANTHROPIC_API_KEY</c> at
    ///     registration time; the provider reads it lazily on first use.
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

        // The Ralph MCP tool classes double as the implementation behind DaedalusKnowledgeTools (fresh scope per invocation).
        services.AddScoped<DaedalusLearningsTools>();
        services.AddScoped<DaedalusFailurePatternsTools>();

        // Sessions left in Running by a crashed host are reset to Idle before the host serves requests.
        services.AddHostedService<AgentSessionCrashRecovery>();

        services.AddThalos(thalos =>
        {
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
