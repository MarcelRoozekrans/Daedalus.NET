using Daedalus.Application.Abstractions;
using Daedalus.Application.Configuration;
using Daedalus.Application.Services;
using Daedalus.Application.Services.CodeAnalysis;
using Daedalus.Domain.Abstractions;
using Daedalus.Infrastructure.Agents;
using Daedalus.Infrastructure.Configuration;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Infrastructure.Persistence.Repositories;
using Daedalus.Infrastructure.Services;
using Daedalus.Infrastructure.Services.CodeAnalysis;
using Daedalus.Infrastructure.Services.Git;
using Daedalus.Infrastructure.Services.NoOp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace Daedalus.Infrastructure.Extensions;

/// <summary>
///     Extension methods for registering Infrastructure services with dependency injection.
/// </summary>
public static class InfrastructureServiceExtensions
{
    /// <summary>
    ///     Registers core infrastructure services including system clock.
    /// </summary>
    public static IServiceCollection AddCoreInfrastructureServices(
        this IServiceCollection services)
    {
        // Register system clock for time-dependent domain logic
        services.AddSingleton<ISystemClock, SystemClock>();

        return services;
    }

    /// <summary>
    ///     Registers external service integrations including MCP agents and workspace context.
    ///     Context7 documentation is available via MCP server tools configured in appsettings.json.
    /// </summary>
    public static IServiceCollection AddExternalServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Register ExternalServices configuration using options pattern with manual binding
        // IL2026: Configuration binding requires reflection for unknown types at runtime
        services.Configure<ExternalServicesConfiguration>(options =>
        {
#pragma warning disable IL2026 // Configuration binding uses reflection; accepted risk for configuration scenarios
            configuration.GetSection(ExternalServicesConfiguration.SectionName).Bind(options);
#pragma warning restore IL2026
        });

        // Register RalphLoop configuration for conditional service registration
        var ralphLoopConfig = new RalphLoopConfiguration();
#pragma warning disable IL2026 // Configuration binding uses reflection; accepted risk for configuration scenarios
        configuration.GetSection(RalphLoopConfiguration.SectionName).Bind(ralphLoopConfig);
#pragma warning restore IL2026

        // Register MCP integration options scoped service
        services.AddScoped(sp =>
        {
            var config = sp.GetRequiredService<IOptions<ExternalServicesConfiguration>>();
            return config.Value.Mcp ?? new McpIntegrationOptions { Enabled = false };
        });

        // Register workspace context provider for loading specs, plan, and agent instructions
        services.AddScoped<IWorkspaceContextProvider, FileSystemWorkspaceContextProvider>();

        // Register real implementations unconditionally — WorkspacePath is set dynamically per-task.
        // Middleware guards on WorkspacePath at runtime (empty = skip).
        services.AddScoped<IGitWorkflowService, GitWorkflowService>();
        services.AddScoped<ILoopbackEvaluator, LoopbackEvaluator>();
        services.AddScoped<IWorkspaceOrchestrator, WorkspaceOrchestrator>();

        services.AddScoped<IPromptContextStore, DatabasePromptContextStore>();

        // Register structured learnings persistence and failure pattern database
        services.AddScoped<ILearningsRepository, LearningsRepository>();
        services.AddScoped<IFailurePatternDatabase, FailurePatternDatabase>();

        // Register brainstorm repository for brainstorm session persistence
        services.AddScoped<IBrainstormRepository, BrainstormRepository>();

        // Register knowledge base tool status for LearningsEnrichmentMiddleware mode switching
        services.AddScoped<IKnowledgeBaseToolStatus, KnowledgeBaseToolStatus>();

        return services;
    }

    /// <summary>
    ///     Registers Agent Framework services: <c>IRalphAgentFactory</c> (Claude via Anthropic),
    ///     <c>McpToolBuilder</c> (MCP → AITool bridge), and supporting infrastructure.
    ///     Call this after AddExternalServices so MCP configuration is available.
    /// </summary>
    public static IServiceCollection AddAgentFrameworkServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // McpToolBuilder converts McpServerConfig entries into AITool instances
        services.AddSingleton<McpToolBuilder>();

        // Register embedding service — try Ollama, fallback to NoOp
        // The IEmbeddingGenerator<string, Embedding<float>> is optional and registered by the API
        // project when Ollama is available
        services.AddScoped<IEmbeddingService>(sp =>
        {
            var generator = sp.GetService<IEmbeddingGenerator<string, Embedding<float>>>();
            if (generator != null)
            {
                return new OllamaEmbeddingService(
                    generator,
                    sp.GetRequiredService<ILogger<OllamaEmbeddingService>>());
            }

            return new NoOpEmbeddingService();
        });

        // IRalphAgentFactory is the primary interface for LLM invocations:
        // - Creates ChatClientAgent backed by Anthropic Claude
        // - Attaches MCP tools automatically
        // - Supports subagent pattern for Ralph Wiggum technique
        services.AddScoped<IRalphAgentFactory, RalphAgentFactory>();

        return services;
    }

    /// <summary>
    ///     Registers code analysis services including Ralph Loop orchestration and Git operations.
    ///     Configures resilient HTTP clients with retry, circuit breaker, and timeout policies.
    /// </summary>
    public static IServiceCollection AddCodeAnalysisServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Register code analysis repository
        services.AddScoped<ICodeAnalysisRepository, CodeAnalysisRepository>();

        // Register Git repository manager
        services.AddScoped<IGitRepositoryManager, GitRepositoryManager>();

        // Register Git connection tester
        services.AddScoped<IGitConnectionTester, GitConnectionTester>();

        // Register repository code extractor
        services.AddScoped<IRepositoryCodeExtractor, RepositoryCodeExtractor>();

        // Register analysis prompt builder
        services.AddScoped<IAnalysisPromptBuilder, AnalysisPromptBuilder>();

        // Register Git change applier
        services.AddScoped<IGitChangeApplier, GitChangeApplier>();

        // Register resilient HTTP clients for GitHub and Azure DevOps API calls
        // Uses Microsoft.Extensions.Http.Resilience (Polly v8) standard resilience pipeline:
        // retry (exponential backoff), circuit breaker, and timeout
        services.AddHttpClient<GitHubPullRequestFactory>(client =>
            {
                client.DefaultRequestHeaders.Add("User-Agent", "Daedalus-RalphLoop");
            })
            .AddStandardResilienceHandler(options =>
            {
                options.Retry.MaxRetryAttempts = 3;
                options.Retry.BackoffType = DelayBackoffType.Exponential;
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(15);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(60);
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
                options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);
            });

        services.AddHttpClient<AzureDevOpsPullRequestFactory>(client =>
            {
                client.DefaultRequestHeaders.Add("User-Agent", "Daedalus-RalphLoop");
            })
            .AddStandardResilienceHandler(options =>
            {
                options.Retry.MaxRetryAttempts = 3;
                options.Retry.BackoffType = DelayBackoffType.Exponential;
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(15);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(60);
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
                options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);
            });

        // Register pull request factory
        services.AddScoped<IPullRequestFactory, PullRequestFactory>();

        // Register repository platform detector
        services.AddScoped<IRepositoryPlatformDetector, RepositoryPlatformDetector>();

        // Register Ralph Loop orchestrator
        services.AddScoped<IRalphLoopOrchestrator, RalphLoopOrchestrator>();

        return services;
    }
}
