using System.Diagnostics.CodeAnalysis;

namespace Daedalus.Application.DTOs;

/// <summary>The sources a cost figure's tokens are read from, and how a session's owner picks one.</summary>
public static class CostSources
{
    /// <summary>Iterations of the task loop that phase 2.8 retired, from <c>TaskExecutions</c>. Read-only history.</summary>
    public const string History = "history";

    /// <summary>Completed agent nodes of manufacture runs, from <c>node-usage</c> records, with the model and cache split.</summary>
    public const string Manufacture = "manufacture";

    /// <summary>Agent sessions a person owns. No model, no cache split.</summary>
    public const string Chat = "chat";

    /// <summary>Agent sessions a scheduled run owns (<c>schedule:*</c>). No model, no cache split.</summary>
    public const string Scheduled = "scheduled";

    /// <summary>The model key of tokens recorded without a model.</summary>
    public const string NoModel = "unattributed";

    /// <summary>The owner prefix of a manufacture node turn's session (<c>WorkflowCaller.Id</c>).</summary>
    public const string WorkflowOwnerPrefix = "workflow:";

    /// <summary>The owner prefix of a scheduled run's session.</summary>
    public const string ScheduleOwnerPrefix = "schedule:";

    /// <summary>
    ///     The source of a session owned by <paramref name="ownerId"/>, or null for a manufacture node turn, which its
    ///     <c>node-usage</c> record already counts.
    /// </summary>
    public static string? ForSessionOwner(string ownerId)
    {
        if (ownerId.StartsWith(WorkflowOwnerPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        return ownerId.StartsWith(ScheduleOwnerPrefix, StringComparison.Ordinal) ? Scheduled : Chat;
    }
}

/// <summary>The overall cost, split by source, by model and by day.</summary>
public record CostSummaryDto(
    long TotalInputTokens,
    long TotalOutputTokens,
    long TotalCacheReadTokens,
    long TotalCacheWriteTokens,
    decimal TotalCost,
    int TotalEntries,
    int UnreadableRecords,
    ExcludedCostDto Excluded,
    IReadOnlyList<CostSliceDto> BySource,
    IReadOnlyList<CostSliceDto> ByModel,
    IReadOnlyList<CostDayDto> ByDay,
    string Scope);

/// <summary>One slice of the total: a source, or a model. <c>Entries</c> counts executions, records or sessions.</summary>
public record CostSliceDto(
    string Key, long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens, decimal Cost, int Entries);

/// <summary>One source's usage on one UTC day.</summary>
public record CostDayDto(DateOnly Day, string Source, long InputTokens, long OutputTokens, decimal Cost, int Entries);

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

/// <summary>Estimated cost for a planned run of up to <c>MaxIterations</c> turns.</summary>
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
