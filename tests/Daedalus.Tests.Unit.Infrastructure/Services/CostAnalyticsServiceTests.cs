using Daedalus.Application.Configuration;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Daedalus.Tests.Unit.Infrastructure.Services;

/// <summary>
///     Unit tests for <see cref="CostAnalyticsService.EstimateCostAsync"/> — the guard against silently relabelling an
///     unpriced model's estimate under another model's name and rate.
/// </summary>
public sealed class CostAnalyticsServiceTests : IAsyncDisposable
{
    private const string PricedModelId = "claude-sonnet-4-20250514";
    private readonly ApplicationDbContext _dbContext;

    public CostAnalyticsServiceTests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new ApplicationDbContext(options);
    }

    public async ValueTask DisposeAsync()
    {
        await _dbContext.Database.EnsureDeletedAsync();
        await _dbContext.DisposeAsync();
    }

    private static IOptions<ModelPricingConfiguration> PricingWithOneModel() =>
        Options.Create(new ModelPricingConfiguration
        {
            Models = new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase)
            {
                [PricedModelId] = new()
                {
                    DisplayName = "Claude Sonnet 4",
                    InputTokenPricePerMillion = 3.0m,
                    OutputTokenPricePerMillion = 15.0m
                }
            }
        });

    [Fact]
    public async Task EstimateCostAsync_WithUnpricedModel_ReturnsFailureNamingTheModel()
    {
        // Falsifiability: reverting CostAnalyticsService.EstimateCostAsync's `if (!_pricing.Models.TryGetValue(...))`
        // branch to the old silent-substitution behaviour (relabel to the first configured entry) turns this red —
        // the result would be a success carrying "claude-sonnet-4-20250514" instead of a failure.
        var sut = new CostAnalyticsService(_dbContext, PricingWithOneModel());

        var result = await sut.EstimateCostAsync("claude-sonnet-5", maxIterations: 10, estimatedPromptTokens: 4000);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("claude-sonnet-5");
    }

    [Fact]
    public async Task EstimateCostAsync_WithPricedModel_ReturnsTheCorrectEstimate()
    {
        // Falsifiability: swapping InputTokenPricePerMillion and OutputTokenPricePerMillion in
        // CostAnalyticsService.EstimateCostAsync's cost formula turns this red — EstimatedMinCost/EstimatedMaxCost
        // would come out as 0.081/0.27 instead of 0.189/0.63. Prompt tokens (1000) are deliberately different from
        // the 4000-token response-token default below, so the two prices can't cancel each other out symmetrically.
        var sut = new CostAnalyticsService(_dbContext, PricingWithOneModel());

        var result = await sut.EstimateCostAsync(PricedModelId, maxIterations: 10, estimatedPromptTokens: 1000);

        result.IsSuccess.Should().BeTrue();
        var estimate = result.Value;
        estimate.ModelId.Should().Be(PricedModelId);
        estimate.ModelDisplayName.Should().Be("Claude Sonnet 4");
        estimate.EstimatedResponseTokens.Should().Be(4000); // no TaskExecutions in the DB -> the 4000-token default
        estimate.EstimatedMinCost.Should().Be(0.189m); // 3 iterations (30% of 10) * (1000*3 + 4000*15)/1_000_000
        estimate.EstimatedMaxCost.Should().Be(0.63m); // 10 iterations * (1000*3 + 4000*15)/1_000_000
    }
}
