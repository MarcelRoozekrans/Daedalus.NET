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
        // The update and delete handlers read the run status. A host whose workflow engine is off (the Console)
        // never calls AddDaedalusAgents, so it needs this fallback; AddDaedalusAgents replaces it on engine hosts,
        // whatever the call order, because it uses Replace.
        services.TryAddSingleton<IWorkflowRunStatusReader, DisabledWorkflowRunStatusReader>();

        // Register the generated IMediator and every request handler by its own concrete type, all scoped.
        // AddMediator is itself internal, so it can only be called from code compiled into
        // Daedalus.Application. The public facade lets the two MVC controllers dispatch across the
        // assembly boundary.
        services.AddMediator(ServiceLifetime.Scoped);
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
}
