namespace Daedalus.Infrastructure.Persistence.Orm.CostAnalytics;

/// <summary>
///     The task loop's executions of one model on one UTC day, as <see cref="CostAnalyticsSql"/> groups them:
///     <paramref name="Day"/> is <c>"ExecutedAt"</c> cut to the day in UTC.
/// </summary>
internal sealed record HistoryDayRow(string? ModelId, DateTime Day, long InputTokens, long OutputTokens, int Entries);

/// <summary>One node-usage record's payload and when it was written; the payload is JSON, read by the caller.</summary>
internal sealed record NodeUsageRecordRow(string PayloadJson, DateTime CreatedAt);

/// <summary>
///     The agent sessions of one owner started on one UTC day, as <see cref="CostAnalyticsSql"/> groups them:
///     <paramref name="Day"/> is <c>"CreatedAt"</c> cut to the day in UTC.
/// </summary>
internal sealed record SessionDayRow(string OwnerId, DateTime Day, long InputTokens, long OutputTokens, int Entries);

/// <summary>The task loop's executions of one model across one project's tasks.</summary>
internal sealed record ProjectModelCostRow(
    Guid ProjectId, string ProjectName, string? ModelId, long InputTokens, long OutputTokens, int ExecutionCount);

/// <summary>The task loop's executions of one model on one task.</summary>
internal sealed record TaskModelCostRow(
    Guid TaskId, string Title, string? ModelId, long InputTokens, long OutputTokens, int ExecutionCount);
