using System.Diagnostics.CodeAnalysis;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Services;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Daedalus.Application.Extensions;

/// <summary>
///     Extension methods for registering application services with dependency injection.
/// </summary>
public static class ApplicationServiceExtensions
{
    /// <summary>
    ///     Registers all application layer services including command and query handlers.
    /// </summary>
    public static IServiceCollection AddApplicationServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Note: RalphLoopConfiguration is registered in the API/Infrastructure layer
        // where full dependency injection setup is available

        // Auto-register all command and query handlers in the Application assembly
        // AOT-compatible: Uses pre-computed handler types instead of runtime reflection
        RegisterHandlersAot(services);

        // Register command and query handler factory (AOT-compatible)
        // The factory resolves handlers without using reflection at runtime
        services.AddScoped<ICommandHandlerFactory>(sp => new CommandQueryHandlerFactory(sp));
        services.AddScoped<IQueryHandlerFactory>(sp => new CommandQueryHandlerFactory(sp));

        // Register prompt building and MCP agent selection
        services.AddPromptBuilding();

        // Register learnings service for cross-task knowledge transfer
        services.AddScoped<ILearningsService, LearningsService>();

        // Register inline LLM response parser (replaces file-based + subagent extraction)
        services.AddScoped<ILlmResponseParser, LlmResponseParser>();

        // Register PRD service
        services.AddScoped<IPrdService, PrdService>();

        // Register phase chaining orchestrator for multi-phase task dependency resolution
        services.AddScoped<IPhaseOrchestrator, PhaseOrchestrator>();

        // Register FluentValidation validators from the Application assembly
        services.AddValidatorsFromAssembly(typeof(ApplicationServiceExtensions).Assembly, ServiceLifetime.Scoped);

        return services;
    }

    /// <summary>
    ///     Registers prompt builder services including the enhanced MCP-integrated builder.
    ///     Note: MCP agent selector and Context7 documentation injector are registered
    ///     in the Infrastructure layer via AddExternalServices() extension.
    /// </summary>
    public static IServiceCollection AddPromptBuilding(this IServiceCollection services)
    {
        // Register the RLP template builder for structured prompt assembly
        services.AddScoped<IRalphPromptTemplateBuilder, RalphPromptTemplateBuilder>();

        // Register the base prompt builder (now with template builder and workspace context)
        services.AddScoped<DefaultPromptBuilder>();

        // Register agent executor for multi-agent execution chains
        services.AddScoped<IAgentExecutor, AgentExecutor>();

        // Register the enhanced prompt builder that combines MCP agents and Context7 docs
        // (MCP implementations are registered in Infrastructure layer)
        services.AddScoped<IPromptBuilder>(sp =>
            new McpEnhancedPromptBuilder(
                sp.GetRequiredService<DefaultPromptBuilder>(),
                sp.GetRequiredService<IMcpAgentSelector>(),
                sp.GetRequiredService<IContext7DocumentationInjector>(),
                sp.GetRequiredService<ILogger<McpEnhancedPromptBuilder>>()));

        return services;
    }

    /// <summary>
    ///     Registers command and query handlers using AOT-compatible approach.
    ///     This method discovers handlers without expensive runtime reflection scanning.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification =
            "Assembly.GetTypes() is required for handler discovery. Trimming should preserve handler types via ICommandHandler and IQueryHandler.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification =
            "GetInterfaces() is used to find handler interfaces. Trimmer will preserve these interface implementations.")]
    [UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification = "Handlers are registered via their interface types which are preserved by the trimmer.")]
    private static void RegisterHandlersAot(IServiceCollection services)
    {
        // Get all types from the Application assembly
        var assembly = typeof(ApplicationServiceExtensions).Assembly;
        var handlers = assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .ToList();

        // Register ICommandHandler<,> implementations
        var commandHandlerType = typeof(ICommandHandler<,>);
        foreach (var handler in handlers)
        {
            var interfaces = handler.GetInterfaces();
            foreach (var iface in interfaces)
            {
                if (iface.IsGenericType &&
                    iface.GetGenericTypeDefinition() == commandHandlerType)
                {
                    // Register the handler for the specific command handler interface
                    services.AddScoped(iface, handler);
                }
            }
        }

        // Register IQueryHandler<,> implementations
        var queryHandlerType = typeof(IQueryHandler<,>);
        foreach (var handler in handlers)
        {
            var interfaces = handler.GetInterfaces();
            foreach (var iface in interfaces)
            {
                if (iface.IsGenericType &&
                    iface.GetGenericTypeDefinition() == queryHandlerType)
                {
                    // Register the handler for the specific query handler interface
                    services.AddScoped(iface, handler);
                }
            }
        }
    }
}
