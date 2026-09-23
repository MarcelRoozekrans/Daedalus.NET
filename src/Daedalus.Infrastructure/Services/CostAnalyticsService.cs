using Daedalus.Application.Abstractions;
using Daedalus.Application.Configuration;
using Daedalus.Application.DTOs;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZeroAlloc.Results;

namespace Daedalus.Infrastructure.Services;

/// <summary>
///     Provides cost analytics by querying TaskExecution data and applying model pricing.
/// </summary>
public sealed class CostAnalyticsService(
    ApplicationDbContext dbContext,
    IOptions<ModelPricingConfiguration> pricingOptions) : ICostAnalyticsService
{
    /// <summary>
    ///     What every cost figure below covers: only Ralph loop iterations persisted as <c>TaskExecution</c> rows
    ///     (written by <c>ExecuteTaskCommandHandler</c> and <c>RalphLoopPipelineService</c>), because every query
    ///     in this service reads <c>TaskExecutions</c> and nothing else.
    /// </summary>
    /// <remarks>
    ///     <b>This string used to credit the wrong reason, and the correction is worth stating.</b> It said agent
    ///     turns run through <c>Daedalus.Agents</c> "are never persisted anywhere". They are:
    ///     <c>PostgresAgentSessionStore.RecordTurnAsync</c> atomically increments <c>AgentSessions.TurnCount</c>,
    ///     <c>TotalInputTokens</c> and <c>TotalOutputTokens</c>, and <c>AgentMessages</c> rows carry per-message
    ///     token counts. The conclusion held for a different reason — this service simply never reads those
    ///     tables — and saying so is the more useful statement, because it names where the data already is for
    ///     anyone who wants to aggregate it.
    /// </remarks>
    private const string Scope =
        "Covers only Ralph loop iterations recorded as TaskExecution rows (written by ExecuteTaskCommandHandler " +
        "and RalphLoopPipelineService): every query behind these figures reads TaskExecutions and nothing else. " +
        "Agent turns run through Daedalus.Agents are recorded separately - AgentSessions carries TurnCount, " +
        "TotalInputTokens and TotalOutputTokens per session and AgentMessages carries per-message token counts - " +
        "and none of that is aggregated here.";

    private readonly ModelPricingConfiguration _pricing = pricingOptions.Value;

    public async Task<CostSummaryDto> GetSummaryAsync(CancellationToken ct = default)
    {
        var perModel = await dbContext.TaskExecutions
            .GroupBy(e => e.ModelId)
            .Select(g => new
            {
                ModelId = g.Key,
                InputTokens = g.Sum(e => (long)e.InputTokens),
                OutputTokens = g.Sum(e => (long)e.OutputTokens),
                ExecutionCount = g.Count()
            })
            .ToListAsync(ct);

        if (perModel.Count == 0)
        {
            return new CostSummaryDto(0, 0, 0m, 0, 0, ExcludedCostDto.None, Scope);
        }

        var totalTasks = await dbContext.TaskExecutions.Select(e => e.TaskId).Distinct().CountAsync(ct);
        var (cost, excluded) = PriceGroups(
            perModel.Select(g => (g.ModelId, g.InputTokens, g.OutputTokens, g.ExecutionCount)));

        return new CostSummaryDto(
            perModel.Sum(g => g.InputTokens),
            perModel.Sum(g => g.OutputTokens),
            cost,
            perModel.Sum(g => g.ExecutionCount),
            totalTasks,
            excluded,
            Scope);
    }

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
                    Scope);
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
                    Scope);
            })
            .ToList();
    }

    public async Task<IReadOnlyList<TaskCostDto>> GetCostsBySessionIdAsync(Guid sessionId, CancellationToken ct = default)
    {
        var raw = await dbContext.TaskExecutions
            .Where(e => e.SessionId == sessionId)
            .Join(dbContext.Tasks,
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
                    Scope);
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
                (unpricedModelIds ??= []).Add(group.ModelId);
                continue;
            }

            cost += group.InputTokens * pricing.InputTokenPricePerMillion / 1_000_000m;
            cost += group.OutputTokens * pricing.OutputTokenPricePerMillion / 1_000_000m;
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
