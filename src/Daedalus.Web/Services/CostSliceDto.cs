namespace Daedalus.Web.Services;

public record CostSliceDto(
    string Key, long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens, decimal Cost, int Entries);
