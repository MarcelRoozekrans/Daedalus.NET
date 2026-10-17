using Microsoft.Extensions.Options;

namespace Daedalus.Application.Configuration;

/// <summary>
///     Configuration for LLM model pricing used in cost calculations.
/// </summary>
public sealed class ModelPricingConfiguration
{
    public const string SectionName = "ModelPricing";

    /// <summary>
    ///     Per-model pricing keyed by model ID (e.g., "claude-sonnet-5").
    /// </summary>
#pragma warning disable CA2227 // Collection properties should be read only - required for IOptions binding
    public IDictionary<string, ModelPricing> Models { get; set; } = new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase);
#pragma warning restore CA2227
}

/// <summary>
///     Pricing for a specific LLM model.
/// </summary>
public sealed class ModelPricing
{
    /// <summary>Human-readable model name (e.g., "Claude Sonnet 4").</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Cost per 1 million input tokens in USD.</summary>
    public decimal InputTokenPricePerMillion { get; set; }

    /// <summary>Cost per 1 million output tokens in USD.</summary>
    public decimal OutputTokenPricePerMillion { get; set; }

    /// <summary>
    ///     Cost per 1 million cache-read input tokens in USD. When unset, <see cref="EffectiveCacheReadTokenPricePerMillion"/>
    ///     derives it as 0.1 times <see cref="InputTokenPricePerMillion"/>, Anthropic's prompt-caching read rate.
    /// </summary>
    public decimal? CacheReadTokenPricePerMillion { get; set; }

    /// <summary>
    ///     Cost per 1 million cache-write input tokens in USD. When unset, <see cref="EffectiveCacheWriteTokenPricePerMillion"/>
    ///     derives it as 1.25 times <see cref="InputTokenPricePerMillion"/>, Anthropic's prompt-caching write rate for the
    ///     5-minute TTL.
    /// </summary>
    public decimal? CacheWriteTokenPricePerMillion { get; set; }

    /// <summary>The cache-read price in force: the configured one, else 0.1 times the input price. The one place the 0.1 lives.</summary>
    public decimal EffectiveCacheReadTokenPricePerMillion => CacheReadTokenPricePerMillion ?? InputTokenPricePerMillion * 0.1m;

    /// <summary>The cache-write price in force: the configured one, else 1.25 times the input price. The one place the 1.25 lives.</summary>
    public decimal EffectiveCacheWriteTokenPricePerMillion => CacheWriteTokenPricePerMillion ?? InputTokenPricePerMillion * 1.25m;
}

/// <summary>
///     Refuses a model price that is negative, so a typo cannot turn spend into a credit. Run at host start through
///     <c>ValidateOnStart</c>, and directly by tests.
/// </summary>
public sealed class ModelPricingConfigurationValidator : IValidateOptions<ModelPricingConfiguration>
{
    public ValidateOptionsResult Validate(string? name, ModelPricingConfiguration options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string>? failures = null;
        foreach (var (modelId, pricing) in options.Models)
        {
            Check(modelId, nameof(ModelPricing.InputTokenPricePerMillion), pricing.InputTokenPricePerMillion);
            Check(modelId, nameof(ModelPricing.OutputTokenPricePerMillion), pricing.OutputTokenPricePerMillion);
            Check(modelId, nameof(ModelPricing.CacheReadTokenPricePerMillion), pricing.CacheReadTokenPricePerMillion);
            Check(modelId, nameof(ModelPricing.CacheWriteTokenPricePerMillion), pricing.CacheWriteTokenPricePerMillion);
        }

        return failures is null
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);

        void Check(string modelId, string property, decimal? value)
        {
            if (value < 0)
            {
                (failures ??= []).Add($"{ModelPricingConfiguration.SectionName}:Models:{modelId}:{property} must not be negative, but is {value}.");
            }
        }
    }
}
