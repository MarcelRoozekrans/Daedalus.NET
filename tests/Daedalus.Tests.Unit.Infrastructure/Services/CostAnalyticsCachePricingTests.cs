using Daedalus.Application.Configuration;
using Daedalus.Application.DTOs;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodeUsage = Daedalus.Domain.Entities.NodeUsage;
using TaskExecution = Daedalus.Domain.Entities.TaskExecution;
using WorkflowRunRecord = Daedalus.Domain.Entities.WorkflowRunRecord;

namespace Daedalus.Tests.Unit.Infrastructure.Services;

/// <summary>
///     Issue #360: a group's input tokens include its cache reads and writes, which Anthropic bills at 0.1 times and 1.25 times
///     the input price. Only the uncached remainder is plain input. Every expected figure below is computed by hand in its comment.
/// </summary>
public sealed class CostAnalyticsCachePricingTests : IAsyncDisposable
{
    private const string Sonnet = "model-two-ten";
    private const string Other = "model-three-fifteen";
    private static readonly DateTime Day = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

    private readonly ApplicationDbContext _db = new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    public async ValueTask DisposeAsync()
    {
        await _db.Database.EnsureDeletedAsync();
        await _db.DisposeAsync();
    }

    private CostAnalyticsService Service(ModelPricing? sonnet = null) => new(_db, Options.Create(new ModelPricingConfiguration
    {
        Models = new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase)
        {
            [Sonnet] = sonnet ?? new() { DisplayName = "Two ten", InputTokenPricePerMillion = 2.0m, OutputTokenPricePerMillion = 10.0m },
            [Other] = new() { DisplayName = "Three fifteen", InputTokenPricePerMillion = 3.0m, OutputTokenPricePerMillion = 15.0m },
        },
    }));

    private void Manufacture(NodeUsage usage) =>
        _db.WorkflowRunRecords.Add(WorkflowRunRecord.Create(
            Guid.NewGuid(), 3, "implement", WorkflowRunRecord.NodeUsageKind, "host", null, usage.ToPayloadJson(), Day).Value);

    private static NodeUsage Cached() => new(Sonnet, 1_000_000, 10_000, 800_000, 150_000);

    /// <summary>
    ///     1,000,000 input of which 800,000 read and 150,000 written, plus 10,000 output, at 2 and 10 per million:
    ///     uncached 50,000 × 2/1M = 0.1; writes 150,000 × 2.5/1M = 0.375; reads 800,000 × 0.2/1M = 0.16; output 10,000 × 10/1M = 0.1.
    ///     Red: price input at the full input rate, as before the fix; the cost is 2.1.
    ///     Red: drop the cache-write term; the cost is 0.36. Red: price writes at the read rate; the cost is 0.46.
    ///     Red: price reads at the input rate; the cost is 1.875.
    /// </summary>
    [Fact]
    public async Task A_group_with_cache_reads_and_writes_is_priced_cache_aware()
    {
        Manufacture(Cached());
        await _db.SaveChangesAsync();

        var summary = await Service().GetSummaryAsync();

        summary.TotalCost.Should().Be(0.735m);
        summary.ByModel.Single(m => string.Equals(m.Key, Sonnet, StringComparison.Ordinal)).Cost.Should().Be(0.735m);
        summary.BySource.Single().Cost.Should().Be(0.735m);
        summary.ByDay.Single().Cost.Should().Be(0.735m);
    }

    /// <summary>
    ///     History rows carry no cache split, so they price as input × 2/1M plus output × 10/1M: 1,000,000 × 2/1M = 2 and
    ///     10,000 × 10/1M = 0.1, so 2.1. This guards the sources with no split.
    ///     Red: take a group with no cache split as having no uncached input; both rows price 0.1 instead of 2.1.
    /// </summary>
    [Fact]
    public async Task A_group_with_no_cache_split_prices_exactly_as_before()
    {
        _db.TaskExecutions.Add(new TaskExecution
        {
            Id = Guid.NewGuid(),
            TaskId = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            IterationNumber = 1,
            InputTokens = 1_000_000,
            OutputTokens = 10_000,
            ModelId = Sonnet,
            ExecutedAt = Day,
        });
        Manufacture(new NodeUsage(Sonnet, 1_000_000, 10_000, 0, 0));
        await _db.SaveChangesAsync();

        var summary = await Service().GetSummaryAsync();

        summary.BySource.Single(s => string.Equals(s.Key, CostSources.History, StringComparison.Ordinal)).Cost.Should().Be(2.1m);
        summary.BySource.Single(s => string.Equals(s.Key, CostSources.Manufacture, StringComparison.Ordinal)).Cost.Should().Be(2.1m);
        summary.TotalCost.Should().Be(4.2m);
    }

    /// <summary>
    ///     A review split over two models is priced part by part. Part one, at 2/10, is the 0.735 above. Part two, at 3/15, has
    ///     100,000 input of which 60,000 read, and 1,000 output: uncached 40,000 × 3/1M = 0.12; reads 60,000 × 0.3/1M = 0.018;
    ///     output 1,000 × 15/1M = 0.015, so 0.153. Together 0.888.
    ///     Red: price each part with its cache tokens zeroed; part one costs 2.1 and part two 0.315.
    /// </summary>
    [Fact]
    public async Task A_per_model_split_is_priced_part_by_part_cache_aware()
    {
        var first = Cached();
        var second = new NodeUsage(Other, 100_000, 1_000, 60_000, 0);
        Manufacture(new NodeUsage(Sonnet, 1_100_000, 11_000, 860_000, 150_000).WithSplit([first, second]).Value);
        await _db.SaveChangesAsync();

        var summary = await Service().GetSummaryAsync();

        var byModel = summary.ByModel.ToDictionary(m => m.Key, StringComparer.Ordinal);
        byModel[Sonnet].Cost.Should().Be(0.735m);
        byModel[Other].Cost.Should().Be(0.153m);
        summary.TotalCost.Should().Be(0.888m);
    }

    /// <summary>
    ///     Configured cache prices win over the derived ones. Both set at 1 and 3: uncached 0.1; writes 150,000 × 3/1M = 0.45;
    ///     reads 800,000 × 1/1M = 0.8; output 0.1, so 1.45. Only reads set to 1: 0.1 + 0.375 + 0.8 + 0.1 = 1.375, writes derived.
    ///     Only writes set to 3: 0.1 + 0.45 + 0.16 + 0.1 = 0.81, reads derived.
    ///     Red: ignore the configured value and always derive; every row returns 0.735.
    ///     Red: let a configured read price also replace the derived write price; the second row is wrong.
    /// </summary>
    [Theory]
    [InlineData(1.0, 3.0, 1.45)]
    [InlineData(1.0, null, 1.375)]
    [InlineData(null, 3.0, 0.81)]
    public async Task Explicit_cache_prices_override_the_derived_ones(double? read, double? write, double expected)
    {
        Manufacture(Cached());
        await _db.SaveChangesAsync();
        var pricing = new ModelPricing
        {
            DisplayName = "Two ten",
            InputTokenPricePerMillion = 2.0m,
            OutputTokenPricePerMillion = 10.0m,
            CacheReadTokenPricePerMillion = (decimal?)read,
            CacheWriteTokenPricePerMillion = (decimal?)write,
        };

        var summary = await Service(pricing).GetSummaryAsync();

        summary.TotalCost.Should().Be((decimal)expected);
    }

    /// <summary>
    ///     A malformed record whose reads and writes exceed its input must not price negative uncached input. Input 100,000,
    ///     reads 80,000, writes 60,000: the remainder is -40,000, clamped to 0, so 60,000 × 2.5/1M = 0.15 plus
    ///     80,000 × 0.2/1M = 0.016, so 0.166. Without the clamp the remainder subtracts 0.08 and the cost is 0.086.
    ///     Red: drop the <c>Math.Max</c> clamp; the cost is 0.086.
    /// </summary>
    [Fact]
    public async Task Uncached_input_never_goes_negative_when_cache_tokens_exceed_input()
    {
        Manufacture(new NodeUsage(Sonnet, 100_000, 0, 80_000, 60_000));
        await _db.SaveChangesAsync();

        var summary = await Service().GetSummaryAsync();

        summary.TotalCost.Should().Be(0.166m);
    }

    /// <summary>
    ///     The pricing endpoint returns the prices in force. Unset, they are 0.1 and 1.25 times the input price: 0.2 and 2.5 at 2.
    ///     Red: change either factor in <c>ModelPricing</c>; the row fails. Red: return the configured nullable as 0 in the DTO
    ///     mapping; the explicit row fails.
    /// </summary>
    [Fact]
    public async Task The_pricing_endpoint_returns_the_effective_cache_prices()
    {
        var derived = (await Service().GetPricingAsync()).Single(p => string.Equals(p.ModelId, Sonnet, StringComparison.Ordinal));
        var explicitPrices = (await Service(new ModelPricing
        {
            DisplayName = "x",
            InputTokenPricePerMillion = 2.0m,
            OutputTokenPricePerMillion = 10.0m,
            CacheReadTokenPricePerMillion = 0.5m,
            CacheWriteTokenPricePerMillion = 4.0m,
        }).GetPricingAsync()).Single(p => string.Equals(p.ModelId, Sonnet, StringComparison.Ordinal));

        derived.CacheReadPricePerMillion.Should().Be(0.2m);
        derived.CacheWritePricePerMillion.Should().Be(2.5m);
        explicitPrices.CacheReadPricePerMillion.Should().Be(0.5m);
        explicitPrices.CacheWritePricePerMillion.Should().Be(4.0m);
    }
}
