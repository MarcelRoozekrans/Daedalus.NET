using System.Diagnostics.CodeAnalysis;

namespace Daedalus.Application.DTOs;

/// <summary>Overall cost summary across all projects.</summary>
public record CostSummaryDto(
    long TotalInputTokens,
    long TotalOutputTokens,
    decimal TotalCost,
    int TotalExecutions,
    int TotalTasks,
    ExcludedCostDto Excluded,
    string Scope);

/// <summary>Cost breakdown for a single project.</summary>
public record ProjectCostDto(
    Guid ProjectId,
    string ProjectName,
    long InputTokens,
    long OutputTokens,
    decimal EstimatedCost,
    int ExecutionCount,
    ExcludedCostDto Excluded,
    string Scope);

/// <summary>Cost breakdown for a single task within a project.</summary>
public record TaskCostDto(
    Guid TaskId,
    string TaskTitle,
    long InputTokens,
    long OutputTokens,
    decimal EstimatedCost,
    int IterationCount,
    ExcludedCostDto Excluded,
    string Scope);

/// <summary>Estimated cost for a planned Ralph run.</summary>
public record CostEstimateDto(
    string ModelId,
    string ModelDisplayName,
    int MaxIterations,
    int EstimatedPromptTokens,
    int EstimatedResponseTokens,
    decimal EstimatedMinCost,
    decimal EstimatedMaxCost);

/// <summary>Model pricing information for UI display.</summary>
public record ModelPricingDto(
    string ModelId,
    string DisplayName,
    decimal InputPricePerMillion,
    decimal OutputPricePerMillion);

/// <summary>
///     Tokens and executions a cost figure could not price, broken out by why, kept separate from
///     <c>EstimatedCost</c>/<c>TotalCost</c> on the containing DTO by <c>CostAnalyticsService.PriceGroups</c>.
///     "Unattributed" means the execution has no recorded model at all
///     (<see cref="Daedalus.Domain.Entities.TaskExecution.ModelId"/> is <see langword="null"/>); "unpriced" means a
///     model was recorded but has no matching entry under <c>ModelPricing:Models</c>. Neither bucket is ever priced
///     at another model's rate.
/// </summary>
public record ExcludedCostDto(
    long UnattributedInputTokens,
    long UnattributedOutputTokens,
    int UnattributedExecutionCount,
    long UnpricedInputTokens,
    long UnpricedOutputTokens,
    int UnpricedExecutionCount,
    IReadOnlyList<string> UnpricedModelIds)
{
    /// <summary>Every execution behind this figure carried a model with a configured price.</summary>
    public static ExcludedCostDto None { get; } = new(0, 0, 0, 0, 0, 0, []);
}
