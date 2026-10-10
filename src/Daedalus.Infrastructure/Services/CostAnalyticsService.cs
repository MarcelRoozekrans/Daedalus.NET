using Daedalus.Application.Abstractions;
using Daedalus.Application.Configuration;
using Daedalus.Application.DTOs;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZeroAlloc.Results;
using NodeUsage = Daedalus.Domain.Entities.NodeUsage;
using WorkflowRunRecord = Daedalus.Domain.Entities.WorkflowRunRecord;

namespace Daedalus.Infrastructure.Services;

/// <summary>
///     Cost analytics over four sources: see <c>SummaryScope</c>.
/// </summary>
public sealed class CostAnalyticsService(
    ApplicationDbContext dbContext,
    IOptions<ModelPricingConfiguration> pricingOptions) : ICostAnalyticsService
{
    /// <summary>What the summary covers. Amendment A3 of the phase 2.8 design.</summary>
    private const string SummaryScope =
        "Four sources. history: iterations of the task loop that phase 2.8 retired, from TaskExecutions. " +
        "manufacture: one node-usage record per completed agent node of a manufacture run, with its model and cache split. " +
        "chat: agent sessions owned by a person. scheduled: agent sessions owned by schedule:*. Sessions owned by " +
        "workflow:* are a manufacture run's node turns; their node-usage records already count them, so they are left out. " +
        "Session sources carry no model and no cache split, so their tokens are unattributed and not priced, and they are " +
        "dated by the day the session started. Input tokens include cache reads and writes and are priced at the model's input rate.";

    /// <summary>What the per-project and per-task tables cover (ruling P7).</summary>
    private const string HistoryScope =
        "Covers only iterations of the task loop that phase 2.8 retired, recorded as TaskExecution rows. A manufacture " +
        "run is not linked to a project's cost table; its usage is in the summary's manufacture source.";

    private readonly ModelPricingConfiguration _pricing = pricingOptions.Value;

    public async Task<CostSummaryDto> GetSummaryAsync(CancellationToken ct = default)
    {
        var (manufacture, unreadable) = await ManufactureAsync(ct).ConfigureAwait(false);
        List<UsageGroup> groups =
        [
            .. await HistoryAsync(ct).ConfigureAwait(false),
            .. manufacture,
            .. await SessionsAsync(ct).ConfigureAwait(false),
        ];

        var (total, excluded) = PriceGroups(groups.Select(g => (g.ModelId, g.InputTokens, g.OutputTokens, g.Entries)));
        return new CostSummaryDto(
            groups.Sum(g => g.InputTokens),
            groups.Sum(g => g.OutputTokens),
            groups.Sum(g => g.CacheReadTokens),
            groups.Sum(g => g.CacheWriteTokens),
            total,
            groups.Sum(g => g.Entries),
            unreadable,
            excluded,
            Slices(groups, g => g.Source),
            Slices(groups, g => g.ModelId ?? CostSources.NoModel),
            [.. groups
                .GroupBy(g => (g.Day, g.Source))
                .OrderBy(d => d.Key.Day).ThenBy(d => d.Key.Source, StringComparer.Ordinal)
                .Select(d => new CostDayDto(d.Key.Day, d.Key.Source, d.Sum(g => g.InputTokens), d.Sum(g => g.OutputTokens),
                    Math.Round(d.Sum(Price), 4), d.Sum(g => g.Entries)))],
            SummaryScope);
    }

    /// <summary>One source's tokens for one model on one day.</summary>
    private sealed record UsageGroup(
        string Source, string? ModelId, DateOnly Day, long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens, int Entries);

    private async Task<List<UsageGroup>> HistoryAsync(CancellationToken ct)
    {
        var rows = await dbContext.TaskExecutions.AsNoTracking()
            .GroupBy(e => new { e.ModelId, Day = e.ExecutedAt.Date })
            .Select(g => new
            {
                g.Key.ModelId,
                g.Key.Day,
                InputTokens = g.Sum(e => (long)e.InputTokens),
                OutputTokens = g.Sum(e => (long)e.OutputTokens),
                Entries = g.Count(),
            })
            .ToListAsync(ct).ConfigureAwait(false);

        return [.. rows.Select(r => new UsageGroup(
            CostSources.History, r.ModelId, DateOnly.FromDateTime(r.Day), r.InputTokens, r.OutputTokens, 0, 0, r.Entries))];
    }

    /// <summary>Node-usage payloads are JSON, so they are read and summed here. There is one per completed agent node.</summary>
    private async Task<(List<UsageGroup> Groups, int Unreadable)> ManufactureAsync(CancellationToken ct)
    {
        var rows = await dbContext.WorkflowRunRecords.AsNoTracking()
            .Where(r => r.Kind == WorkflowRunRecord.NodeUsageKind)
            .Select(r => new { r.PayloadJson, r.CreatedAt })
            .ToListAsync(ct).ConfigureAwait(false);

        var unreadable = 0;
        var read = new List<(NodeUsage Usage, DateOnly Day)>(rows.Count);
        foreach (var row in rows)
        {
            var usage = NodeUsage.FromPayloadJson(row.PayloadJson);
            if (usage.IsFailure)
            {
                unreadable++;
                continue;
            }

            read.Add((usage.Value, DateOnly.FromDateTime(row.CreatedAt)));
        }

        var groups = read
            .GroupBy(r => (r.Usage.Model, r.Day))
            .Select(g => new UsageGroup(
                CostSources.Manufacture, g.Key.Model, g.Key.Day,
                g.Sum(r => r.Usage.InputTokens), g.Sum(r => r.Usage.OutputTokens),
                g.Sum(r => r.Usage.CacheReadTokens), g.Sum(r => r.Usage.CacheWriteTokens), g.Count()))
            .ToList();
        return (groups, unreadable);
    }

    /// <summary>
    ///     Grouped by owner in the database and classified here, because a prefix test is not translated the same way by
    ///     every provider. There are few owners.
    /// </summary>
    private async Task<List<UsageGroup>> SessionsAsync(CancellationToken ct)
    {
        var rows = await dbContext.AgentSessions.AsNoTracking()
            .GroupBy(s => new { s.OwnerId, Day = s.CreatedAt.Date })
            .Select(g => new
            {
                g.Key.OwnerId,
                g.Key.Day,
                InputTokens = g.Sum(s => s.TotalInputTokens),
                OutputTokens = g.Sum(s => s.TotalOutputTokens),
                Entries = g.Count(),
            })
            .ToListAsync(ct).ConfigureAwait(false);

        return [.. rows
            .Select(r => (Source: CostSources.ForSessionOwner(r.OwnerId), Row: r))
            .Where(x => x.Source is not null)
            .GroupBy(x => (x.Source!, DateOnly.FromDateTime(x.Row.Day)))
            .Select(g => new UsageGroup(
                g.Key.Item1, null, g.Key.Item2, g.Sum(x => x.Row.InputTokens), g.Sum(x => x.Row.OutputTokens), 0, 0, g.Sum(x => x.Row.Entries)))];
    }

    private IReadOnlyList<CostSliceDto> Slices(List<UsageGroup> groups, Func<UsageGroup, string> key) =>
        [.. groups
            .GroupBy(key, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new CostSliceDto(
                g.Key, g.Sum(x => x.InputTokens), g.Sum(x => x.OutputTokens), g.Sum(x => x.CacheReadTokens),
                g.Sum(x => x.CacheWriteTokens), Math.Round(g.Sum(Price), 4), g.Sum(x => x.Entries)))];

    /// <summary>The group's cost at its own model's rate; 0 when the model is unattributed or unpriced (see <see cref="PriceGroups"/>).</summary>
    private decimal Price(UsageGroup group) =>
        group.ModelId is not null && _pricing.Models.TryGetValue(group.ModelId, out var pricing)
            ? PriceTokens(pricing, group.InputTokens, group.OutputTokens)
            : 0m;

    /// <summary>The one place a model's rate turns tokens into money.</summary>
    private static decimal PriceTokens(ModelPricing pricing, long inputTokens, long outputTokens) =>
        (inputTokens * pricing.InputTokenPricePerMillion / 1_000_000m) + (outputTokens * pricing.OutputTokenPricePerMillion / 1_000_000m);

    public async Task<IReadOnlyList<ProjectCostDto>> GetCostsByProjectAsync(CancellationToken ct = default)
    {
        var raw = await dbContext.TaskExecutions
            .Join(dbContext.Tasks,
                e => e.TaskId,
                t => t.Id,
                (e, t) => new { Execution = e, Task = t })
            .Join(dbContext.Projects,
                et => et.Task.ProjectId,
                p => p.Id,
                (et, p) => new { et.Execution, et.Task, Project = p })
            .GroupBy(x => new { x.Project.Id, x.Project.ProjectName, x.Execution.ModelId })
            .Select(g => new
            {
                g.Key.Id,
                g.Key.ProjectName,
                g.Key.ModelId,
                InputTokens = g.Sum(x => (long)x.Execution.InputTokens),
                OutputTokens = g.Sum(x => (long)x.Execution.OutputTokens),
                ExecutionCount = g.Count()
            })
            .ToListAsync(ct);

        return raw
            .GroupBy(r => new { r.Id, r.ProjectName })
            .Select(g =>
            {
                var (cost, excluded) = PriceGroups(g.Select(r => (r.ModelId, r.InputTokens, r.OutputTokens, r.ExecutionCount)));
                return new ProjectCostDto(
                    g.Key.Id,
                    g.Key.ProjectName,
                    g.Sum(r => r.InputTokens),
                    g.Sum(r => r.OutputTokens),
                    cost,
                    g.Sum(r => r.ExecutionCount),
                    excluded,
                    HistoryScope);
            })
            .ToList();
    }

    public async Task<IReadOnlyList<TaskCostDto>> GetCostsByProjectIdAsync(Guid projectId, CancellationToken ct = default)
    {
        var raw = await dbContext.TaskExecutions
            .Join(dbContext.Tasks.Where(t => t.ProjectId == projectId),
                e => e.TaskId,
                t => t.Id,
                (e, t) => new { Execution = e, Task = t })
            .GroupBy(x => new { x.Task.Id, x.Task.Title, x.Execution.ModelId })
            .Select(g => new
            {
                g.Key.Id,
                g.Key.Title,
                g.Key.ModelId,
                InputTokens = g.Sum(x => (long)x.Execution.InputTokens),
                OutputTokens = g.Sum(x => (long)x.Execution.OutputTokens),
                ExecutionCount = g.Count()
            })
            .ToListAsync(ct);

        return raw
            .GroupBy(r => new { r.Id, r.Title })
            .Select(g =>
            {
                var (cost, excluded) = PriceGroups(g.Select(r => (r.ModelId, r.InputTokens, r.OutputTokens, r.ExecutionCount)));
                return new TaskCostDto(
                    g.Key.Id,
                    g.Key.Title,
                    g.Sum(r => r.InputTokens),
                    g.Sum(r => r.OutputTokens),
                    cost,
                    g.Sum(r => r.ExecutionCount),
                    excluded,
                    HistoryScope);
            })
            .ToList();
    }

    public async Task<Result<CostEstimateDto>> EstimateCostAsync(
        string modelId, int maxIterations, int estimatedPromptTokens, CancellationToken ct = default)
    {
        var avgOutputTokens = await dbContext.TaskExecutions
            .Where(e => e.OutputTokens > 0)
            .AverageAsync(e => (double?)e.OutputTokens, ct) ?? 4000.0;

        var estimatedResponseTokens = (int)avgOutputTokens;

        if (!_pricing.Models.TryGetValue(modelId, out var pricing))
        {
            return Result<CostEstimateDto>.Failure(
                $"No pricing is configured for model '{modelId}'. Add it to ModelPricing:Models before estimating its cost.");
        }

        var inputCostPerIteration = estimatedPromptTokens * pricing.InputTokenPricePerMillion / 1_000_000m;
        var outputCostPerIteration = estimatedResponseTokens * pricing.OutputTokenPricePerMillion / 1_000_000m;
        var costPerIteration = inputCostPerIteration + outputCostPerIteration;

        var minIterations = Math.Max(1, (int)(maxIterations * 0.3));
        var estimatedMinCost = Math.Round(costPerIteration * minIterations, 4);
        var estimatedMaxCost = Math.Round(costPerIteration * maxIterations, 4);

        return Result<CostEstimateDto>.Success(new CostEstimateDto(
            modelId,
            pricing.DisplayName,
            maxIterations,
            estimatedPromptTokens,
            estimatedResponseTokens,
            estimatedMinCost,
            estimatedMaxCost));
    }

    public Task<IReadOnlyList<ModelPricingDto>> GetPricingAsync(CancellationToken ct = default)
    {
        var result = _pricing.Models
            .Select(kvp => new ModelPricingDto(
                kvp.Key,
                kvp.Value.DisplayName,
                kvp.Value.InputTokenPricePerMillion,
                kvp.Value.OutputTokenPricePerMillion))
            .ToList() as IReadOnlyList<ModelPricingDto>;

        return Task.FromResult(result);
    }

    /// <summary>
    ///     Prices each (model, tokens) group independently at that model's own configured rate and sums only the
    ///     groups a rate exists for. A group with no attributed model, or an attributed model absent from
    ///     <c>ModelPricing:Models</c>, is never priced at another model's rate - it is folded into the returned
    ///     <see cref="ExcludedCostDto"/> instead, so the cost total can never silently absorb tokens it could not
    ///     price.
    /// </summary>
    private (decimal Cost, ExcludedCostDto Excluded) PriceGroups(
        IEnumerable<(string? ModelId, long InputTokens, long OutputTokens, int ExecutionCount)> groups)
    {
        var cost = 0m;
        long unattributedInput = 0;
        long unattributedOutput = 0;
        var unattributedCount = 0;
        long unpricedInput = 0;
        long unpricedOutput = 0;
        var unpricedCount = 0;
        List<string>? unpricedModelIds = null;

        foreach (var group in groups)
        {
            if (group.ModelId is null)
            {
                unattributedInput += group.InputTokens;
                unattributedOutput += group.OutputTokens;
                unattributedCount += group.ExecutionCount;
                continue;
            }

            if (!_pricing.Models.TryGetValue(group.ModelId, out var pricing))
            {
                unpricedInput += group.InputTokens;
                unpricedOutput += group.OutputTokens;
                unpricedCount += group.ExecutionCount;
                unpricedModelIds ??= [];
                if (!unpricedModelIds.Contains(group.ModelId, StringComparer.Ordinal))
                {
                    unpricedModelIds.Add(group.ModelId);
                }

                continue;
            }

            cost += PriceTokens(pricing, group.InputTokens, group.OutputTokens);
        }

        var excluded = unattributedCount == 0 && unpricedCount == 0
            ? ExcludedCostDto.None
            : new ExcludedCostDto(
                unattributedInput,
                unattributedOutput,
                unattributedCount,
                unpricedInput,
                unpricedOutput,
                unpricedCount,
                (IReadOnlyList<string>?)unpricedModelIds ?? []);

        return (Math.Round(cost, 4), excluded);
    }
}
