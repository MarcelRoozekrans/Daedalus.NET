using System.Diagnostics.CodeAnalysis;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Daedalus.Application.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.Validation;

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

        // Register the generated IMediator (internal to this assembly - AddMediator() is itself
        // internal, so it can only be called from code compiled into Daedalus.Application) and the
        // public facade that lets the two MVC controllers dispatch across the assembly boundary.
        services.AddMediator();
        services.AddScoped<IApplicationCommands, ApplicationCommands>();

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

        // Register the ZeroAlloc.Validation-generated validators. ZeroAlloc.Validation.Inject's
        // AddZeroAllocValidators() only discovers `class`-declared [Validate] targets — its
        // generator filters on ClassDeclarationSyntax, which a `record` (positional or otherwise)
        // does not satisfy, verified against the shipped 1.7.3 assembly. Every validated DTO here
        // is a record, so the bulk registration would find nothing (and the extension method
        // itself would not even be emitted). Register the eight generated validators explicitly
        // instead; they are stateless, so singleton is safe.
        services.AddSingleton<ValidatorFor<CreateProjectDto>, CreateProjectDtoValidator>();
        services.AddSingleton<ValidatorFor<UpdateProjectDto>, UpdateProjectDtoValidator>();
        services.AddSingleton<ValidatorFor<CreateTaskDto>, CreateTaskDtoValidator>();
        services.AddSingleton<ValidatorFor<UpdateTaskDto>, UpdateTaskDtoValidator>();
        services.AddSingleton<ValidatorFor<CreateRepositoryConfigurationDto>, CreateRepositoryConfigurationDtoValidator>();
        services.AddSingleton<ValidatorFor<UpdateRepositoryConfigurationDto>, UpdateRepositoryConfigurationDtoValidator>();
        services.AddSingleton<ValidatorFor<SendBrainstormMessageDto>, SendBrainstormMessageDtoValidator>();
        services.AddSingleton<ValidatorFor<SubmitAnalysisRequest>, SubmitAnalysisRequestValidator>();

        return services;
    }

    /// <summary>
    ///     Registers prompt builder services.
    ///     Library documentation is available on-demand via Context7 MCP tools
    ///     (resolve-library-id, get-library-docs) which Claude calls during inference.
    /// </summary>
    public static IServiceCollection AddPromptBuilding(this IServiceCollection services)
    {
        // Register the RLP template builder for structured prompt assembly
        services.AddScoped<IRalphPromptTemplateBuilder, RalphPromptTemplateBuilder>();

        // Register the prompt builder directly — Context7 documentation is now fetched
        // on-demand by Claude via MCP tools, no decorator needed
        services.AddScoped<DefaultPromptBuilder>();
        services.AddScoped<IPromptBuilder>(sp => sp.GetRequiredService<DefaultPromptBuilder>());

        // Register agent executor for multi-agent execution chains
        services.AddScoped<IAgentExecutor, AgentExecutor>();

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
