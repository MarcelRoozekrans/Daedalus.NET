using Microsoft.Extensions.DependencyInjection;

namespace Daedalus.Api.Endpoints;

/// <summary>
///     Composition of the minimal API endpoints. They sit beside MVC while the controllers move over: a route is
///     served by exactly one of the two, and a controller is deleted in the task that maps its endpoints.
/// </summary>
public static class DaedalusEndpoints
{
    /// <summary>
    ///     Points the endpoints' JSON at <see cref="ApiJsonSerializerContext"/> alone. The resolver chain is cleared
    ///     first, so there is no reflection fallback: a type the context does not list fails instead of serializing
    ///     by reflection. This changes <c>Microsoft.AspNetCore.Http.Json.JsonOptions</c> only; MVC reads its own.
    /// </summary>
    public static IServiceCollection AddDaedalusEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.TypeInfoResolverChain.Clear();
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, ApiJsonSerializerContext.Default);
        });

        return services;
    }

    /// <summary>
    ///     Maps the minimal API endpoints. Each port task adds its <c>Map…Endpoints(app)</c> call here.
    /// </summary>
    public static IEndpointRouteBuilder MapDaedalusEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app;
    }
}
