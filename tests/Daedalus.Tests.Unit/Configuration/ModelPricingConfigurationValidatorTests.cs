using Daedalus.Application.Configuration;

namespace Daedalus.Tests.Unit.Configuration;

/// <summary>Issue #360: a negative price is refused when the host starts, cache prices included.</summary>
public sealed class ModelPricingConfigurationValidatorTests
{
    private static ModelPricingConfiguration With(ModelPricing pricing) =>
        new() { Models = new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase) { ["m"] = pricing } };

    private static ModelPricing Valid() => new() { DisplayName = "m", InputTokenPricePerMillion = 2m, OutputTokenPricePerMillion = 10m };

    /// <summary>
    ///     Red: delete the cache-read check from the validator; the first row passes validation. Red: delete the cache-write
    ///     check; the second row passes.
    /// </summary>
    [Fact]
    public void A_negative_cache_price_is_refused()
    {
        var read = Valid();
        read.CacheReadTokenPricePerMillion = -0.01m;
        var write = Valid();
        write.CacheWriteTokenPricePerMillion = -1m;
        var validator = new ModelPricingConfigurationValidator();

        var readResult = validator.Validate(null, With(read));
        var writeResult = validator.Validate(null, With(write));

        readResult.Failed.Should().BeTrue();
        readResult.FailureMessage.Should().Contain("CacheReadTokenPricePerMillion").And.Contain("m");
        writeResult.Failed.Should().BeTrue();
        writeResult.FailureMessage.Should().Contain("CacheWriteTokenPricePerMillion");
    }

    /// <summary>
    ///     Unset and zero cache prices are legitimate: unset derives, zero is free.
    ///     Red: refuse values at or below zero, or a null; this fails.
    /// </summary>
    [Fact]
    public void Unset_and_zero_cache_prices_are_accepted()
    {
        var zero = Valid();
        zero.CacheReadTokenPricePerMillion = 0m;
        zero.CacheWriteTokenPricePerMillion = 0m;
        var validator = new ModelPricingConfigurationValidator();

        validator.Validate(null, With(Valid())).Succeeded.Should().BeTrue();
        validator.Validate(null, With(zero)).Succeeded.Should().BeTrue();
    }

    /// <summary>Red: delete the input and output checks; the row passes validation.</summary>
    [Fact]
    public void A_negative_input_or_output_price_is_refused()
    {
        var input = Valid();
        input.InputTokenPricePerMillion = -1m;
        var output = Valid();
        output.OutputTokenPricePerMillion = -1m;
        var validator = new ModelPricingConfigurationValidator();

        validator.Validate(null, With(input)).Failed.Should().BeTrue();
        validator.Validate(null, With(output)).Failed.Should().BeTrue();
    }
}
