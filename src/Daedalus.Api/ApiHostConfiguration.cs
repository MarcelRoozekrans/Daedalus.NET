using Daedalus.Application.Configuration;
using Microsoft.Extensions.Options;

namespace Daedalus.Api;

/// <summary>
///     The configuration the Api host binds itself, outside any library's registration: model pricing and the CORS
///     origins. Kept out of the top-level statements so each bind is a product call a test can make.
/// </summary>
internal static class ApiHostConfiguration
{
    /// <summary>The configuration key holding the origins the <c>RestrictedCors</c> policy allows.</summary>
    internal const string CorsOriginsKey = "Cors:AllowedOrigins";

    /// <summary>Registers <see cref="ModelPricingConfiguration"/>, bound from its section and validated on start.</summary>
    internal static IServiceCollection AddModelPricing(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ModelPricingConfiguration>(options =>
            configuration.GetSection(ModelPricingConfiguration.SectionName).Bind(options));
        services.AddSingleton<IValidateOptions<ModelPricingConfiguration>, ModelPricingConfigurationValidator>();
        services.AddOptions<ModelPricingConfiguration>().ValidateOnStart();
        return services;
    }

    /// <summary>The origins the <c>RestrictedCors</c> policy allows; none when the key is absent.</summary>
    internal static string[] ReadCorsOrigins(IConfiguration configuration) =>
        configuration
            .GetSection(CorsOriginsKey)
            .Get<string[]>() ?? Array.Empty<string>();
}
