namespace Daedalus.Web.Services;

public record CostDayDto(DateOnly Day, string Source, long InputTokens, long OutputTokens, decimal Cost, int Entries);
