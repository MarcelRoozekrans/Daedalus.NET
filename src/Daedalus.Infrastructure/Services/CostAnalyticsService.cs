using Daedalus.Application.Abstractions;
using Daedalus.Application.Configuration;
using Daedalus.Application.DTOs;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Daedalus.Infrastructure.Services;

/// <summary>
///     Provides cost analytics by querying TaskExecution data and applying model pricing.
/// </summary>
public sealed class CostAnalyticsService(
    ApplicationDbContext dbContext,
    IOptions<ModelPricingConfiguration> pricingOptions) : ICostAnalyticsService
{
    private readonly ModelPricingConfiguration _pricing = pricingOptions.Value;

    public async Task<CostSummaryDto> GetSummaryAsync(CancellationToken ct = default)
    {
        var stats = await dbContext.TaskExecutions
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalInputTokens = g.Sum(e => (long)e.InputTokens),
                TotalOutputTokens = g.Sum(e => (long)e.OutputTokens),
                TotalExecutions = g.Count(),
                TotalTasks = g.Select(e => e.TaskId).Distinct().Count()
            })
            .FirstOrDefaultAsync(ct);

        if (stats is null)
        {
            return new CostSummaryDto(0, 0, 0m, 0, 0);
        }

        var totalCost = CalculateCostFromExecutions(stats.TotalInputTokens, stats.TotalOutputTokens);

        return new CostSummaryDto(
            stats.TotalInputTokens,
            stats.TotalOutputTokens,
            totalCost,
            stats.TotalExecutions,
            stats.TotalTasks);
    }

    public async Task<IReadOnlyList<ProjectCostDto>> GetCostsByProjectAsync(CancellationToken ct = default)
    {
        var projectCosts = await dbContext.TaskExecutions
            .Join(dbContext.Tasks,
                e => e.TaskId,
                t => t.Id,
                (e, t) => new { Execution = e, Task = t })
            .Join(dbContext.Projects,
                et => et.Task.ProjectId,
                p => p.Id,
                (et, p) => new { et.Execution, et.Task, Project = p })
            .GroupBy(x => new { x.Project.Id, x.Project.ProjectName })
            .Select(g => new ProjectCostDto(
                g.Key.Id,
                g.Key.ProjectName,
                g.Sum(x => (long)x.Execution.InputTokens),
                g.Sum(x => (long)x.Execution.OutputTokens),
                0m,
                g.Count()))
            .ToListAsync(ct);

        return projectCosts
            .Select(p => p with { EstimatedCost = CalculateCostFromExecutions(p.InputTokens, p.OutputTokens) })
            .ToList();
    }

    public async Task<IReadOnlyList<TaskCostDto>> GetCostsByProjectIdAsync(Guid projectId, CancellationToken ct = default)
    {
        var taskCosts = await dbContext.TaskExecutions
            .Join(dbContext.Tasks.Where(t => t.ProjectId == projectId),
                e => e.TaskId,
                t => t.Id,
                (e, t) => new { Execution = e, Task = t })
            .GroupBy(x => new { x.Task.Id, x.Task.Title })
            .Select(g => new TaskCostDto(
                g.Key.Id,
                g.Key.Title,
                g.Sum(x => (long)x.Execution.InputTokens),
                g.Sum(x => (long)x.Execution.OutputTokens),
                0m,
                g.Count()))
            .ToListAsync(ct);

        return taskCosts
            .Select(t => t with { EstimatedCost = CalculateCostFromExecutions(t.InputTokens, t.OutputTokens) })
            .ToList();
    }

    public async Task<IReadOnlyList<TaskCostDto>> GetCostsBySessionIdAsync(Guid sessionId, CancellationToken ct = default)
    {
        var taskCosts = await dbContext.TaskExecutions
            .Where(e => e.SessionId == sessionId)
            .Join(dbContext.Tasks,
                e => e.TaskId,
                t => t.Id,
                (e, t) => new { Execution = e, Task = t })
            .GroupBy(x => new { x.Task.Id, x.Task.Title })
            .Select(g => new TaskCostDto(
                g.Key.Id,
                g.Key.Title,
                g.Sum(x => (long)x.Execution.InputTokens),
                g.Sum(x => (long)x.Execution.OutputTokens),
                0m,
                g.Count()))
            .ToListAsync(ct);

        return taskCosts
            .Select(t => t with { EstimatedCost = CalculateCostFromExecutions(t.InputTokens, t.OutputTokens) })
            .ToList();
    }

    public async Task<CostEstimateDto> EstimateCostAsync(
        string modelId, int maxIterations, int estimatedPromptTokens, CancellationToken ct = default)
    {
        var avgOutputTokens = await dbContext.TaskExecutions
            .Where(e => e.OutputTokens > 0)
            .AverageAsync(e => (double?)e.OutputTokens, ct) ?? 4000.0;

        var estimatedResponseTokens = (int)avgOutputTokens;

        if (!_pricing.Models.TryGetValue(modelId, out var pricing))
        {
            var first = _pricing.Models.FirstOrDefault();
            modelId = first.Key ?? modelId;
            pricing = first.Value ?? new ModelPricing
            {
                DisplayName = modelId,
                InputTokenPricePerMillion = 3.0m,
                OutputTokenPricePerMillion = 15.0m
            };
        }

        var inputCostPerIteration = estimatedPromptTokens * pricing.InputTokenPricePerMillion / 1_000_000m;
        var outputCostPerIteration = estimatedResponseTokens * pricing.OutputTokenPricePerMillion / 1_000_000m;
        var costPerIteration = inputCostPerIteration + outputCostPerIteration;

        var minIterations = Math.Max(1, (int)(maxIterations * 0.3));
        var estimatedMinCost = Math.Round(costPerIteration * minIterations, 4);
        var estimatedMaxCost = Math.Round(costPerIteration * maxIterations, 4);

        return new CostEstimateDto(
            modelId,
            pricing.DisplayName,
            maxIterations,
            estimatedPromptTokens,
            estimatedResponseTokens,
            estimatedMinCost,
            estimatedMaxCost);
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

    private decimal CalculateCostFromExecutions(long inputTokens, long outputTokens)
    {
        var pricing = _pricing.Models.Values.FirstOrDefault();
        if (pricing is null)
        {
            return 0m;
        }

        var inputCost = inputTokens * pricing.InputTokenPricePerMillion / 1_000_000m;
        var outputCost = outputTokens * pricing.OutputTokenPricePerMillion / 1_000_000m;
        return Math.Round(inputCost + outputCost, 4);
    }
}
