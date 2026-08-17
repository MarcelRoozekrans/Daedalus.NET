namespace Daedalus.Web.Services;

public record CostSummaryDto(
    long TotalInputTokens,
    long TotalOutputTokens,
    decimal TotalCost,
    int TotalExecutions,
    int TotalTasks);
