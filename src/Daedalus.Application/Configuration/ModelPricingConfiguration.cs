namespace Daedalus.Application.Configuration;

/// <summary>
///     Configuration for LLM model pricing used in cost calculations.
/// </summary>
public sealed class ModelPricingConfiguration
{
    public const string SectionName = "ModelPricing";

    /// <summary>
    ///     Per-model pricing keyed by model ID (e.g., "claude-sonnet-4-20250514").
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
}
