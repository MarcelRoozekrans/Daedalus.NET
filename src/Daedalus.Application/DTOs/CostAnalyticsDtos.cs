using System.Diagnostics.CodeAnalysis;

namespace Daedalus.Application.DTOs;

/// <summary>Overall cost summary across all projects.</summary>
public record CostSummaryDto(
    long TotalInputTokens,
    long TotalOutputTokens,
    decimal TotalCost,
    int TotalExecutions,
    int TotalTasks);

/// <summary>Cost breakdown for a single project.</summary>
public record ProjectCostDto(
    Guid ProjectId,
    string ProjectName,
    long InputTokens,
    long OutputTokens,
    decimal EstimatedCost,
    int ExecutionCount);

/// <summary>Cost breakdown for a single task within a project.</summary>
public record TaskCostDto(
    Guid TaskId,
    string TaskTitle,
    long InputTokens,
    long OutputTokens,
    decimal EstimatedCost,
    int IterationCount);

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
