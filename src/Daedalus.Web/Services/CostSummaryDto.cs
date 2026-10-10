namespace Daedalus.Web.Services;

public record CostSummaryDto(
    long TotalInputTokens,
    long TotalOutputTokens,
    long TotalCacheReadTokens,
    long TotalCacheWriteTokens,
    decimal TotalCost,
    int TotalEntries,
    int UnreadableRecords,
    IReadOnlyList<CostSliceDto> BySource,
    IReadOnlyList<CostSliceDto> ByModel,
    IReadOnlyList<CostDayDto> ByDay,
    string Scope);
