using Daedalus.Application.Configuration;
using Daedalus.Application.DTOs;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using AgentSession = Daedalus.Domain.Entities.AgentSession;
using NodeUsage = Daedalus.Domain.Entities.NodeUsage;
using TaskExecution = Daedalus.Domain.Entities.TaskExecution;
using WorkflowRunRecord = Daedalus.Domain.Entities.WorkflowRunRecord;

namespace Daedalus.Tests.Unit.Infrastructure.Services;

/// <summary>
///     Phase 2.8, amendment A3: the summary covers four sources. Ralph's history comes from <c>TaskExecutions</c>,
///     manufacture from <c>node-usage</c> records, and chat and scheduled turns from <c>AgentSessions</c>. Sessions owned
///     by <c>workflow:*</c> are a manufacture run's node turns, already counted from their records, and are left out.
/// </summary>
public sealed class CostAnalyticsSourcesTests : IAsyncDisposable
{
    private const string ModelA = "model-a";
    private const string ModelB = "model-b";
    private static readonly DateTime Day1 = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Day2 = new(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc);

    private readonly ApplicationDbContext _db = new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    public async ValueTask DisposeAsync()
    {
        await _db.Database.EnsureDeletedAsync();
        await _db.DisposeAsync();
    }

    private CostAnalyticsService Service() => new(_db, Options.Create(new ModelPricingConfiguration
    {
        Models = new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase)
        {
            [ModelA] = new() { DisplayName = "Model A", InputTokenPricePerMillion = 3.0m, OutputTokenPricePerMillion = 15.0m },
            [ModelB] = new() { DisplayName = "Model B", InputTokenPricePerMillion = 1.0m, OutputTokenPricePerMillion = 5.0m },
        },
    }));

    private void History(string? model, int input, int output, DateTime at) =>
        _db.TaskExecutions.Add(new TaskExecution
        {
            Id = Guid.NewGuid(),
            TaskId = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            IterationNumber = 1,
            InputTokens = input,
            OutputTokens = output,
            ModelId = model,
            ExecutedAt = at,
        });

    private void Manufacture(NodeUsage usage, DateTime at) =>
        _db.WorkflowRunRecords.Add(WorkflowRunRecord.Create(
            Guid.NewGuid(), 3, "implement", WorkflowRunRecord.NodeUsageKind, "host", null, usage.ToPayloadJson(), at).Value);

    private void Session(string owner, int input, int output, DateTime at)
    {
        var session = AgentSession.Create(Guid.NewGuid(), Guid.NewGuid(), owner, at).Value;
        session.RecordTurn(input, output, at);
        _db.AgentSessions.Add(session);
    }

    private async Task SeedOneOfEachAsync()
    {
        History(ModelA, 1_000, 100, Day1);
        Manufacture(new NodeUsage(ModelB, 2_000, 200, 500, 50), Day1);
        Session("alice", 300, 30, Day1);
        Session("schedule:daily-digest", 400, 40, Day1);
        Session("workflow:manufacture:7f1c2a8e-0000-0000-0000-000000000001", 9_999, 999, Day1);
        await _db.SaveChangesAsync();
    }

    /// <summary>
    ///     Red: drop the <c>workflow:</c> exclusion; the chat slice and the totals include 9 999.
    ///     Red: classify <c>schedule:*</c> as chat; there is no scheduled slice.
    ///     Red: skip the node-usage query; there is no manufacture slice.
    /// </summary>
    [Fact]
    public async Task Each_source_is_counted_under_its_own_key_and_workflow_sessions_are_left_out()
    {
        await SeedOneOfEachAsync();

        var summary = await Service().GetSummaryAsync();

        summary.BySource.Select(s => (s.Key, s.InputTokens, s.OutputTokens)).Should().BeEquivalentTo(new[]
        {
            (CostSources.History, 1_000L, 100L),
            (CostSources.Manufacture, 2_000L, 200L),
            (CostSources.Chat, 300L, 30L),
            (CostSources.Scheduled, 400L, 40L),
        });
        summary.TotalInputTokens.Should().Be(3_700);
        summary.TotalOutputTokens.Should().Be(370);
        summary.TotalEntries.Should().Be(4);
    }

    /// <summary>
    ///     Each model is priced at its own rate. Session tokens carry no model, so they are unattributed and unpriced.
    ///     Red: price a null model at the first configured rate; the unattributed slice's cost is no longer 0.
    ///     Red: price the node-usage input at model-a's rate; the model-b cost changes.
    /// </summary>
    [Fact]
    public async Task Session_tokens_are_unattributed_and_each_model_is_priced_at_its_own_rate()
    {
        await SeedOneOfEachAsync();

        var summary = await Service().GetSummaryAsync();

        var byModel = summary.ByModel.ToDictionary(m => m.Key, StringComparer.Ordinal);
        byModel[ModelA].Cost.Should().Be(0.0045m);        // 1000 × 3/1M + 100 × 15/1M
        byModel[ModelB].Cost.Should().Be(0.003m);         // 2000 × 1/1M + 200 × 5/1M
        byModel[CostSources.NoModel].Cost.Should().Be(0m);
        byModel[CostSources.NoModel].InputTokens.Should().Be(700);
        summary.Excluded.UnattributedInputTokens.Should().Be(700);
        summary.TotalCost.Should().Be(0.0075m);
    }

    /// <summary>Red: drop the cache mapping from the node-usage read; both cache totals are 0.</summary>
    [Fact]
    public async Task The_manufacture_source_carries_the_cache_split()
    {
        await SeedOneOfEachAsync();

        var summary = await Service().GetSummaryAsync();

        summary.TotalCacheReadTokens.Should().Be(500);
        summary.TotalCacheWriteTokens.Should().Be(50);
        summary.BySource.Single(s => string.Equals(s.Key, CostSources.Manufacture, StringComparison.Ordinal)).CacheReadTokens.Should().Be(500);
    }

    /// <summary>Red: group the history query by model only; the two days collapse into one row.</summary>
    [Fact]
    public async Task Usage_is_split_by_day_and_source()
    {
        History(ModelA, 1_000, 100, Day1);
        History(ModelA, 2_000, 200, Day2);
        Session("alice", 300, 30, Day2);
        await _db.SaveChangesAsync();

        var summary = await Service().GetSummaryAsync();

        summary.ByDay.Select(d => (d.Day, d.Source, d.InputTokens)).Should().Equal(
            (DateOnly.FromDateTime(Day1), CostSources.History, 1_000L),
            (DateOnly.FromDateTime(Day2), CostSources.Chat, 300L),
            (DateOnly.FromDateTime(Day2), CostSources.History, 2_000L));
    }

    /// <summary>
    ///     No source has rows: every figure is zero and every list is empty, not an error.
    ///     Red: emit a zero slice for every <c>CostSources</c> constant; <c>BySource</c> is no longer empty.
    /// </summary>
    [Fact]
    public async Task An_empty_database_is_all_zero()
    {
        var summary = await Service().GetSummaryAsync();

        summary.TotalInputTokens.Should().Be(0);
        summary.TotalCost.Should().Be(0m);
        summary.BySource.Should().BeEmpty();
        summary.ByModel.Should().BeEmpty();
        summary.ByDay.Should().BeEmpty();
    }

    /// <summary>
    ///     A record that does not read as a node usage is counted as unreadable, never as zero tokens, and never thrown.
    ///     Red: skip unreadable payloads without counting them; <c>UnreadableRecords</c> is 0.
    /// </summary>
    [Fact]
    public async Task An_unreadable_node_usage_record_is_counted_as_unreadable()
    {
        _db.WorkflowRunRecords.Add(WorkflowRunRecord.Create(
            Guid.NewGuid(), 3, "implement", WorkflowRunRecord.NodeUsageKind, "host", null, """{"model":"m"}""", Day1).Value);
        await _db.SaveChangesAsync();

        var summary = await Service().GetSummaryAsync();

        summary.UnreadableRecords.Should().Be(1);
        summary.TotalInputTokens.Should().Be(0);
    }

    /// <summary>
    ///     An unpriced model is listed once, however many days and sources it appears in.
    ///     Red: remove the de-duplication in <c>PriceGroups</c>; the model id is listed four times.
    /// </summary>
    [Fact]
    public async Task An_unpriced_model_is_listed_once_across_days_and_sources()
    {
        History("model-unknown", 100, 10, Day1);
        History("model-unknown", 100, 10, Day2);
        Manufacture(new NodeUsage("model-unknown", 200, 20, 0, 0), Day1);
        Manufacture(new NodeUsage("model-unknown", 200, 20, 0, 0), Day2);
        await _db.SaveChangesAsync();

        var summary = await Service().GetSummaryAsync();

        summary.Excluded.UnpricedModelIds.Should().Equal("model-unknown");
        summary.Excluded.UnpricedInputTokens.Should().Be(600);
    }

    /// <summary>Red: return <c>Chat</c> for a <c>workflow:</c> owner; the first row fails.</summary>
    [Theory]
    [InlineData("workflow:manufacture:1", null)]
    [InlineData("schedule:daily-digest", CostSources.Scheduled)]
    [InlineData("alice", CostSources.Chat)]
    [InlineData("telegram:42", CostSources.Chat)]
    public void A_session_owner_maps_to_its_source(string owner, string? source) =>
        CostSources.ForSessionOwner(owner).Should().Be(source);
}
