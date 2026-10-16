using Daedalus.Application.Configuration;
using Daedalus.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace Daedalus.Tests.Unit.Infrastructure.Configuration;

/// <summary>
///     #385: the model PRD generation, brainstorm and code analysis call must have a price, or their spend is left out of
///     every cost total. The API host checks it at start, as the decisions code does for its models.
/// </summary>
public sealed class ClaudeModelPricedValidatorTests
{
    private static ClaudeModelPricedValidator Validator(params string[] priced) =>
        new(Options.Create(new ModelPricingConfiguration
        {
            Models = priced.ToDictionary(
                m => m,
                m => new ModelPricing { DisplayName = m, InputTokenPricePerMillion = 2m, OutputTokenPricePerMillion = 10m },
                StringComparer.OrdinalIgnoreCase),
        }));

    /// <summary>
    ///     Red: return Success unconditionally; the unpriced model passes.
    ///     Red: leave the model out of the failure; the message does not name it.
    /// </summary>
    [Fact]
    public void An_unpriced_model_is_refused_and_named()
    {
        var result = Validator("claude-sonnet-5").Validate(null, new ClaudeConfiguration { Model = "claude-retired" });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("claude-retired").And.Contain("ModelPricing:Models");
    }

    /// <summary>Red: refuse every model; the priced one fails.</summary>
    [Fact]
    public void A_priced_model_is_accepted()
    {
        Validator("claude-sonnet-5").Validate(null, new ClaudeConfiguration { Model = "claude-sonnet-5" })
            .Succeeded.Should().BeTrue();
    }
}
