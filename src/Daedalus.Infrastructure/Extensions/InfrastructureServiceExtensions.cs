using System.Net;
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
using Daedalus.Infrastructure.Services.GitHub;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
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

        // Register MCP integration options scoped service
        services.AddScoped(sp =>
        {
            var config = sp.GetRequiredService<IOptions<ExternalServicesConfiguration>>();
            return config.Value.Mcp ?? new McpIntegrationOptions { Enabled = false };
        });

        // Register failure pattern database
        services.AddScoped<IFailurePatternDatabase, FailurePatternDatabase>();

        // Register brainstorm repository for brainstorm session persistence
        services.AddScoped<IBrainstormRepository, BrainstormRepository>();

        return services;
    }

    /// <summary>
    ///     Registers Agent Framework services: <c>IAgentFactory</c> (Claude via Anthropic),
    ///     <c>McpToolBuilder</c> (MCP → AITool bridge), and supporting infrastructure.
    ///     Call this after AddExternalServices so MCP configuration is available.
    /// </summary>
    public static IServiceCollection AddAgentFrameworkServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // The Anthropic settings AgentFactory reads. With no model of its own it calls Thalos' default model, and with
        // neither it refuses to start rather than call a model nobody chose (#385).
#pragma warning disable IL2026 // Configuration binding uses reflection; accepted risk for configuration scenarios
        services.AddOptions<ClaudeConfiguration>()
            .Bind(configuration.GetSection(ClaudeConfiguration.SectionName))
            .PostConfigure(claude =>
            {
                if (string.IsNullOrWhiteSpace(claude.Model))
                {
                    claude.Model = configuration[ClaudeConfiguration.FallbackModelKey];
                }
            })
            .Validate(
                claude => !string.IsNullOrWhiteSpace(claude.Model),
                $"No Anthropic model is configured. Set {ClaudeConfiguration.FallbackModelKey}, " +
                $"or {ClaudeConfiguration.SectionName}:Model for this client alone.")
            .ValidateOnStart();
#pragma warning restore IL2026

        // McpToolBuilder converts McpServerConfig entries into AITool instances
        services.AddSingleton<McpToolBuilder>();

        // IAgentFactory is the primary interface for LLM invocations:
        // - Creates ChatClientAgent backed by Anthropic Claude
        // - Attaches MCP tools automatically
        services.AddScoped<IAgentFactory, AgentFactory>();

        return services;
    }

    /// <summary>
    ///     Registers code analysis services including the iterative analysis orchestration (issue #279) and Git operations.
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

        // Register repository authentication provider (used by AzureDevOpsPullRequestFactory). Pre-existing gap,
        // not introduced by this change: IRepositoryAuthenticationProvider and its only implementation have
        // existed since before this branch, but nothing ever registered the mapping, so PullRequestFactory could
        // never construct AzureDevOpsPullRequestFactory in the real composition root. It was masked because
        // PullRequestFactory's constructor resolves GitHubPullRequestFactory (its first parameter) before
        // AzureDevOpsPullRequestFactory (its second), so the container always failed on the GitHub side first --
        // fixing that side alone surfaced this one underneath it.
        services.AddScoped<IRepositoryAuthenticationProvider, RepositoryAuthenticationProvider>();

        // Register the single GitHub HTTP client (GitHubApi) that GitHubPullRequestFactory now delegates to,
        // with the same resilience pipeline its own HttpClient used to carry directly.
        services.AddGitHubApi(configuration);

        // GitHubPullRequestFactory itself still needs a registration: AddHttpClient<GitHubPullRequestFactory>()
        // used to register the concrete type as a side effect of giving it an HttpClient. Now that it takes
        // GitHubApi instead, that side effect is gone, so it is registered explicitly here -- otherwise
        // PullRequestFactory (which takes it as a constructor parameter, not behind an interface) fails to
        // resolve, and IPullRequestFactory/IRalphLoopOrchestrator both fail with it.
        services.AddTransient<GitHubPullRequestFactory>();

        services.AddAzureDevOpsPullRequestFactory();

        // Register pull request factory
        services.AddScoped<IPullRequestFactory, PullRequestFactory>();

        // Register repository platform detector
        services.AddScoped<IRepositoryPlatformDetector, RepositoryPlatformDetector>();

        // Register the iterative code-analysis orchestrator (issue #279)
        services.AddScoped<IRalphLoopOrchestrator, RalphLoopOrchestrator>();

        return services;
    }

    /// <summary>
    ///     Registers the single GitHub HTTP client — <see cref="GitHubApi"/> — and its two read/write interfaces,
    ///     plus <see cref="GitHubOptions"/> and the token source. Called both from here (for
    ///     <see cref="GitHubPullRequestFactory"/>, so PR creation works on any host that registers code analysis
    ///     services alone) and from Daedalus.Agents' agent wiring (for the <c>daedalus__*</c>/<c>repoaction__*</c>
    ///     tool classes, so reads and comments work on any host that registers agents alone). A host that calls
    ///     both ends up calling this twice; the guard below makes the second call a no-op rather than a second set of named
    ///     clients and their handlers.
    /// </summary>
    public static IServiceCollection AddGitHubApi(this IServiceCollection services, IConfiguration configuration)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(GitHubApi)))
        {
            return services;
        }

        // IL2026: Configuration binding uses reflection; accepted risk for configuration scenarios (matches the
        // pattern used for ExternalServicesConfiguration above).
#pragma warning disable IL2026
        services.Configure<GitHubOptions>(configuration.GetSection(GitHubOptions.SectionName));
#pragma warning restore IL2026

        // Explicit factory rather than type registration: GitHubTokenSource has a second constructor taking the
        // environment lookup as a delegate (so tests need not mutate process state), and nothing should depend on
        // which one the container's constructor selection happens to pick.
        services.TryAddSingleton<IGitHubTokenSource>(_ => new GitHubTokenSource());

        // Whichever caller registers this first wins; both mean TimeProvider.System.
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<GitHubConditionalCache>();
        services.TryAddTransient<GitHubConditionalCacheHandler>();
        services.TryAddTransient<GitHubRateLimitObserver>();

        // Two named clients behind one facade, so that writes can have a pipeline of their own. GitHubApi sets each
        // client's BaseAddress from GitHubOptions.ApiUrl.
        // Reads, outside in: the conditional cache, the resilience pipeline, which retries what is transient except a
        // 429 (spec amendment A1), then the rate-limit observer. The cache sits outside, so a retried attempt keeps its
        // If-None-Match and a 304 is replayed once. The observer sits innermost, so it sees every attempt's headers,
        // 304s included, and a replay from the cache never reaches it: no reading is counted twice.
        var reads = services.AddHttpClient(GitHubApi.ReadClientName);
        reads.AddHttpMessageHandler<GitHubConditionalCacheHandler>();
        reads.AddStandardResilienceHandler(options =>
        {
            ConfigureResilience(options);
            RetryReadsExceptRateLimits(options);
        });
        reads.AddHttpMessageHandler<GitHubRateLimitObserver>();

        // A write goes out once. GitHub has no idempotency keys, so a comment, issue or pull request sent again after a
        // timeout or a 5xx can land twice. The recovery that is safe stays with the callers: file-findings finds its own
        // earlier output by its marker, and the publisher looks for an open pull request before it opens one.
        var writes = services.AddHttpClient(GitHubApi.WriteClientName);
        writes.AddStandardResilienceHandler(options =>
        {
            ConfigureResilience(options);
            NeverRetry(options);
        });
        writes.AddHttpMessageHandler<GitHubRateLimitObserver>();

        services.AddTransient<GitHubApi>();
        services.AddScoped<IGitHubReader>(sp => sp.GetRequiredService<GitHubApi>());
        services.AddScoped<IGitHubWriter>(sp => sp.GetRequiredService<GitHubApi>());

        return services;
    }

    /// <summary>
    ///     Registers <see cref="AzureDevOpsPullRequestFactory"/>'s typed client. Its one call is a POST that opens a pull
    ///     request, so its pipeline never retries: a create sent twice opens two.
    /// </summary>
    public static IServiceCollection AddAzureDevOpsPullRequestFactory(this IServiceCollection services)
    {
        services.AddHttpClient<AzureDevOpsPullRequestFactory>(client => client.DefaultRequestHeaders.Add("User-Agent", "Daedalus"))
            .AddStandardResilienceHandler(options =>
            {
                ConfigureResilience(options);
                NeverRetry(options);
            });

        return services;
    }

    /// <summary>The standard pipeline every GitHub and Azure DevOps client starts from.</summary>
    private static void ConfigureResilience(HttpStandardResilienceOptions options)
    {
        options.Retry.MaxRetryAttempts = 3;
        options.Retry.BackoffType = DelayBackoffType.Exponential;
        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(15);
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(60);
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
        options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    ///     Keeps the standard pipeline's timeouts, circuit breaker and limiter, and turns its retry off: the predicate
    ///     handles no outcome. <c>DisableForUnsafeHttpMethods</c> would say this per method, but it is
    ///     <c>[Experimental("EXTEXP0001")]</c> in every Microsoft.Extensions.Http.Resilience 10.x release, so a client
    ///     whose calls are all writes gets this pipeline instead.
    /// </summary>
    private static void NeverRetry(HttpStandardResilienceOptions options) =>
        options.Retry.ShouldHandle = static _ => PredicateResult.False();

    /// <summary>
    ///     Retries what the standard pipeline treats as transient, except a 429. Waiting out GitHub's Retry-After would
    ///     block a call for up to the total timeout and then fail it as a timeout with no retry time; resending without
    ///     one sends refused requests. The one-argument <c>IsTransient</c> is stable; its overload with a
    ///     <c>CancellationToken</c> is <c>[Experimental("EXTEXP0001")]</c> and is not used.
    /// </summary>
    private static void RetryReadsExceptRateLimits(HttpStandardResilienceOptions options) =>
        options.Retry.ShouldHandle = static args => ValueTask.FromResult(
            args.Outcome.Result?.StatusCode != HttpStatusCode.TooManyRequests
            && HttpClientResiliencePredicates.IsTransient(args.Outcome));
}
