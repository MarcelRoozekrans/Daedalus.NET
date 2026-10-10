using System.Diagnostics.CodeAnalysis;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Daedalus.Application.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ZeroAlloc.Mediator;
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
        // Auto-register all Mediator request handlers in the Application assembly by their own
        // concrete type. The generated MediatorService resolves each handler with
        // GetRequiredService<TConcreteHandler>() (not by an ICommandHandler/IQueryHandler-style
        // interface), so that is what must be registered here.
        RegisterHandlersAot(services);

        // The update and delete handlers read the run status. A host whose workflow engine is off (the Console)
        // never calls AddDaedalusAgents, so it needs this fallback; AddDaedalusAgents replaces it on engine hosts,
        // whatever the call order, because it uses Replace.
        services.TryAddSingleton<IWorkflowRunStatusReader, DisabledWorkflowRunStatusReader>();

        // Register the generated IMediator (internal to this assembly - AddMediator() is itself
        // internal, so it can only be called from code compiled into Daedalus.Application) and the
        // public facade that lets the two MVC controllers dispatch across the assembly boundary.
        services.AddMediator();
        services.AddScoped<IApplicationCommands, ApplicationCommands>();

        // Register PRD service
        services.AddScoped<IPrdService, PrdService>();

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
    ///     Registers Mediator request handlers using an AOT-compatible approach.
    ///     This method discovers handlers without expensive runtime reflection scanning.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification =
            "Assembly.GetTypes() is required for handler discovery. Trimming should preserve handler types via IRequestHandler.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification =
            "GetInterfaces() is used to find handler interfaces. Trimmer will preserve these interface implementations.")]
    [UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification = "Handlers are registered by their own concrete type, which is preserved by the trimmer.")]
    private static void RegisterHandlersAot(IServiceCollection services)
    {
        // Get all types from the Application assembly
        var assembly = typeof(ApplicationServiceExtensions).Assembly;
        var handlers = assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .ToList();

        // Register IRequestHandler<,> implementations by their own concrete type. The generated
        // MediatorService resolves a handler with GetRequiredService<TConcreteHandler>(), not by
        // the IRequestHandler interface, so registering by interface here would leave every
        // handler unresolvable at runtime.
        var requestHandlerType = typeof(IRequestHandler<,>);
        foreach (var handler in handlers)
        {
            var isRequestHandler = false;
            foreach (var iface in handler.GetInterfaces())
            {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == requestHandlerType)
                {
                    isRequestHandler = true;
                    break;
                }
            }

            if (isRequestHandler)
            {
                services.AddScoped(handler);
            }
        }
    }
}
