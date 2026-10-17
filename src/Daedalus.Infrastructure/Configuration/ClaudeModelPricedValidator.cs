using Daedalus.Application.Configuration;
using Microsoft.Extensions.Options;

namespace Daedalus.Infrastructure.Configuration;

/// <summary>
///     Refuses an Anthropic model with no <c>ModelPricing:Models</c> price, so its spend is never left out of the cost
///     totals. Run at host start through <c>ValidateOnStart</c> by a host that prices models, and directly by tests.
/// </summary>
public sealed class ClaudeModelPricedValidator(IOptions<ModelPricingConfiguration> pricing) : IValidateOptions<ClaudeConfiguration>
{
    public ValidateOptionsResult Validate(string? name, ClaudeConfiguration options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Model is { } model && !pricing.Value.Models.ContainsKey(model)
            ? ValidateOptionsResult.Fail(
                $"The Anthropic model '{model}' has no price. Add it to {ModelPricingConfiguration.SectionName}:Models, " +
                $"or configure a priced model in {ClaudeConfiguration.FallbackModelKey}.")
            : ValidateOptionsResult.Success;
    }
}
