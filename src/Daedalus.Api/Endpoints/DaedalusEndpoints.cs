using System.Diagnostics.CodeAnalysis;
using System.Text.Encodings.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Daedalus.Api.Endpoints;

/// <summary>
///     Composition of the minimal API endpoints, which serve every route of the API. They replaced the MVC
///     controllers one by one, with no change to the contract, and the host maps no controller since.
/// </summary>
/// <remarks>
///     <para>
///     <b>For a new group.</b> Each <c>Map…Endpoints</c> maps its group with <see cref="MapApiGroup"/>, never with a
///     bare <c>MapGroup</c>: that adds <see cref="ZeroAllocValidationEndpointFilter"/>, which answers the binding
///     problems <see cref="ApiBody{T}"/> and <see cref="ApiQuery{T}"/> carry and runs the ZeroAlloc validators, and,
///     with <c>requiresWorkflowEngine</c>, <see cref="WorkflowEngineEnabledEndpointFilter"/> ahead of it.
///     <c>EndpointGroupTests</c> fails for an endpoint under <c>api/</c> without the group's
///     <see cref="DaedalusEndpointGroup"/> metadata. A group that answers its problems as <c>application/json</c>,
///     as the controllers with <c>[Produces("application/json")]</c> did, adds
///     <c>.WithMetadata(ProblemContentType.Json)</c>.
///     </para>
/// </remarks>
public static class DaedalusEndpoints
{
    /// <summary>
    ///     <c>MvcOptions.DefaultMaxModelBindingRecursionDepth</c>, the <c>MaxDepth</c> MVC's <c>JsonOptions</c> read and
    ///     write with; the Http options' default, 0, means 64.
    /// </summary>
    internal const int MvcMaxDepth = 32;

    /// <summary>
    ///     Points the endpoints' JSON at <see cref="ApiJsonSerializerContext"/> alone and gives it MVC's settings. The
    ///     resolver chain is cleared first, so there is no reflection fallback: a type the context does not list fails
    ///     instead of serializing by reflection. MVC reads and writes at a depth of 32, so that is set. The encoder is
    ///     left alone: the Http options already default to <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>,
    ///     which MVC's output formatter also writes with, so an apostrophe goes out raw (R19, R33). This changes
    ///     <c>Microsoft.AspNetCore.Http.Json.JsonOptions</c> only. Also registers the two endpoint filters.
    /// </summary>
    public static IServiceCollection AddDaedalusEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.TypeInfoResolverChain.Clear();
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, ApiJsonSerializerContext.Default);
            options.SerializerOptions.MaxDepth = MvcMaxDepth;
        });
        services.AddSingleton<ZeroAllocValidationEndpointFilter>();
        services.AddSingleton<WorkflowEngineEnabledEndpointFilter>();

        return services;
    }

    /// <summary>
    ///     Maps the minimal API endpoints. Each group's <c>Map…Endpoints(app)</c> call is here, and each of those
    ///     maps its group through <see cref="MapApiGroup"/>.
    /// </summary>
    public static IEndpointRouteBuilder MapDaedalusEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapAgentEndpoints();
        app.MapSchedulesEndpoints();
        app.MapTaskExecutionEndpoints();
        app.MapCostAnalyticsEndpoints();
        app.MapPrdEndpoints();
        app.MapProjectsEndpoints();
        app.MapBrainstormEndpoints();
        app.MapRepositoriesEndpoints();
        app.MapAgentMemoriesEndpoints();
        app.MapAgentSessionsEndpoints();
        app.MapTasksEndpoints();
        app.MapCodeAnalysisEndpoints();
        app.MapWorkflowRunsEndpoints();

        return app;
    }

    /// <summary>
    ///     A route group with the endpoints' filters: the workflow-engine check first when
    ///     <paramref name="requiresWorkflowEngine"/> is set, as MVC's resource filter ran before model binding, then
    ///     <see cref="ZeroAllocValidationEndpointFilter"/>. The filters are the singletons
    ///     <see cref="AddDaedalusEndpoints"/> registers, resolved once per endpoint when it is built.
    /// </summary>
    internal static RouteGroupBuilder MapApiGroup(
        this IEndpointRouteBuilder app, [StringSyntax("Route")] string prefix, bool requiresWorkflowEngine = false)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup(prefix);
        group.WithMetadata(DaedalusEndpointGroup.Instance);
        if (requiresWorkflowEngine)
        {
            group.AddEndpointFilterFactory(static (factory, next) =>
            {
                var filter = factory.ApplicationServices.GetRequiredService<WorkflowEngineEnabledEndpointFilter>();
                return context => filter.InvokeAsync(context, next);
            });
        }

        group.AddEndpointFilterFactory(static (factory, next) =>
        {
            var filter = factory.ApplicationServices.GetRequiredService<ZeroAllocValidationEndpointFilter>();
            return context => filter.InvokeAsync(context, next);
        });

        return group;
    }
}
