using Daedalus.Application.Abstractions;
using Daedalus.Application.Configuration;
using Daedalus.Application.Services;
using Daedalus.Application.Services.Middleware;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Daedalus.Application.Extensions;

/// <summary>
///     Extension methods for registering the Ralph loop middleware pipeline.
/// </summary>
public static class RalphLoopMiddlewareExtensions
{
    /// <summary>
    ///     Registers the Ralph loop middleware pipeline.
    ///     Note: Null Object Pattern implementations must be registered separately if needed.
    ///     See InfrastructureServiceExtensions.AddExternalServices() for optional service registration.
    /// </summary>
    public static IServiceCollection AddRalphLoopMiddleware(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // How much the enrichment/MCP/adapter paths recall. Bound here because every host that runs Ralph calls this
        // method; AddDaedalusAgents/AddDaedalusMemory TryAdd the same instance type from the same configuration key.
        var recallConfig = new RalphRecallConfiguration();
#pragma warning disable IL2026 // Configuration binding requires reflection; acceptable for configuration scenarios
        configuration.GetSection(RalphRecallConfiguration.SectionName).Bind(recallConfig);
#pragma warning restore IL2026
        services.TryAddSingleton(recallConfig);

        // Register middleware components in the pipeline
        services.AddScoped<IRalphLoopMiddleware, LearningsEnrichmentMiddleware>();
        services.AddScoped<IRalphLoopMiddleware, PromptBuildingMiddleware>();
        services.AddScoped<IRalphLoopMiddleware, LlmInvocationMiddleware>();
        services.AddScoped<IRalphLoopMiddleware, CodeChangeApplicationMiddleware>();
        services.AddScoped<IRalphLoopMiddleware, CompletionDetectionMiddleware>();
        services.AddScoped<IRalphLoopMiddleware, InlineLearningsExtractionMiddleware>();
        services.AddScoped<IRalphLoopMiddleware, LoopbackEvaluationMiddleware>();
        services.AddScoped<IRalphLoopMiddleware, GitCheckpointMiddleware>();

        // Register the pipeline service
        services.AddScoped<IRalphLoopService>(sp =>
        {
            var configSection = configuration.GetSection(RalphLoopConfiguration.SectionName);
            var loopConfig = new RalphLoopConfiguration();
#pragma warning disable IL2026 // Configuration binding requires reflection; acceptable for configuration scenarios
            configSection.Bind(loopConfig);
#pragma warning restore IL2026

            return new RalphLoopPipelineService(
                sp.GetRequiredService<IEnumerable<IRalphLoopMiddleware>>(),
                sp.GetRequiredService<IPromptBuilder>(),
                sp.GetRequiredService<ITaskRepository>(),
                sp.GetRequiredService<ILearningsService>(),
                sp.GetRequiredService<ILogger<RalphLoopPipelineService>>(),
                loopConfig);
        });

        return services;
    }
}
