using Daedalus.Application.DTOs;

namespace Daedalus.Application.Abstractions;

/// <summary>
///     Service for querying cost analytics data across projects and executions.
/// </summary>
public interface ICostAnalyticsService
{
    /// <summary>Get overall cost summary across all projects.</summary>
    Task<CostSummaryDto> GetSummaryAsync(CancellationToken ct = default);

    /// <summary>Get cost breakdown per project.</summary>
    Task<IReadOnlyList<ProjectCostDto>> GetCostsByProjectAsync(CancellationToken ct = default);

    /// <summary>Get per-task cost breakdown for a specific project.</summary>
    Task<IReadOnlyList<TaskCostDto>> GetCostsByProjectIdAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>Get per-task cost breakdown for a specific session.</summary>
    Task<IReadOnlyList<TaskCostDto>> GetCostsBySessionIdAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>Estimate cost for a planned Ralph run.</summary>
    Task<CostEstimateDto> EstimateCostAsync(string modelId, int maxIterations, int estimatedPromptTokens, CancellationToken ct = default);

    /// <summary>Get all configured model pricing.</summary>
    Task<IReadOnlyList<ModelPricingDto>> GetPricingAsync(CancellationToken ct = default);
}
