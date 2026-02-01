using System.Globalization;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Configuration;
using Daedalus.Application.Services;
using Daedalus.Application.Services.CodeAnalysis;
using Daedalus.Domain.Abstractions;
using Daedalus.Infrastructure.Configuration;
using Daedalus.Infrastructure.ExternalServices.Context7;
using Daedalus.Infrastructure.ExternalServices.Mcp;
using Daedalus.Infrastructure.Llm;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Infrastructure.Services;
using Daedalus.Infrastructure.Services.CodeAnalysis;
using Daedalus.Infrastructure.Services.Git;
using Daedalus.Infrastructure.Services.NoOp;
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
    ///     Registers external service integrations including MCP agents and Context7 documentation.
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

        // Register MCP session manager for Ralph loop MCP integration
        services.AddSingleton<McpSessionManager>();

        // Register MCP agent selector (no-op stub; replace with a real implementation when needed)
        services.AddScoped<IMcpAgentSelector, NoOpMcpAgentSelector>();

        // Register Context7 documentation injector for library documentation
        // Uses AddHttpClient to provide HttpClient via IHttpClientFactory
        services.AddHttpClient<IContext7DocumentationInjector, Context7DocumentationInjector>();

        // Register workspace context provider for loading specs, plan, and agent instructions
        services.AddScoped<IWorkspaceContextProvider, FileSystemWorkspaceContextProvider>();

        // Register Ralph Wiggum article services (git checkpoints, loop-back, plan management, context persistence)
        // Use Null Object Pattern when features are disabled to avoid null checks throughout the codebase
        if (ralphLoopConfig.EnableGitCheckpoints && !string.IsNullOrEmpty(ralphLoopConfig.WorkspacePath))
        {
            services.AddScoped<IGitWorkflowService, GitWorkflowService>();
        }
        else
        {
            services.AddSingleton<IGitWorkflowService, NoOpGitWorkflowService>();
        }

        if (ralphLoopConfig.EnableLoopbackEvaluation && !string.IsNullOrEmpty(ralphLoopConfig.WorkspacePath))
        {
            services.AddScoped<ILoopbackEvaluator, LoopbackEvaluator>();
        }
        else
        {
            services.AddSingleton<ILoopbackEvaluator, NoOpLoopbackEvaluator>();
        }

        services.AddScoped<IPromptContextStore, DatabasePromptContextStore>();

        // Register structured learnings persistence and failure pattern database
        services.AddScoped<ILearningsRepository, LearningsRepository>();
        services.AddScoped<IFailurePatternDatabase, FailurePatternDatabase>();

        return services;
    }

    /// <summary>
    ///     Registers LLM services including the factory and all configured providers.
    ///     Call this after AddExternalServices to ensure configuration is available.
    /// </summary>
    public static IServiceCollection AddLlmServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Read LLM configuration
        var llmSection = configuration.GetSection("ExternalServices:Llm");
        var defaultProvider = llmSection["Provider"] ?? "copilot";

        // Register Claude if configured and enabled
        var claudeSection = llmSection.GetSection("Claude");
        var claudeEnabled = bool.TryParse(claudeSection["Enabled"], out var enabled) && enabled;
        var claudeApiKey = claudeSection["ApiKey"]
                           ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");

        if (claudeEnabled && !string.IsNullOrWhiteSpace(claudeApiKey))
        {
            var claudeModel = claudeSection["Model"] ?? "claude-sonnet-4-20250514";
            var claudeMaxTokens = int.TryParse(
                claudeSection["MaxTokens"], CultureInfo.InvariantCulture, out var maxTok)
                ? maxTok
                : 8192;
            var claudeTimeout = int.TryParse(
                claudeSection["Timeout"], CultureInfo.InvariantCulture, out var timeout)
                ? TimeSpan.FromMilliseconds(timeout)
                : TimeSpan.FromSeconds(60);

            services.AddSingleton<ClaudeLlmService>(sp =>
                ClaudeLlmService.CreateWithApiKey(
                    claudeApiKey,
                    claudeModel,
                    claudeMaxTokens,
                    claudeTimeout,
                    sp.GetRequiredService<ILogger<ClaudeLlmService>>()));

            services.AddSingleton<ILlmService>(sp => sp.GetRequiredService<ClaudeLlmService>());
        }

        // Register the LlmServiceFactory
        services.AddSingleton<ILlmServiceFactory>(sp =>
            new LlmServiceFactory(
                sp.GetServices<ILlmService>(),
                defaultProvider,
                sp.GetRequiredService<ILogger<LlmServiceFactory>>()));

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
