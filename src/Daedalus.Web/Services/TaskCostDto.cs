namespace Daedalus.Web.Services;

public record TaskCostDto(
    Guid TaskId,
    string TaskTitle,
    long InputTokens,
    long OutputTokens,
    decimal EstimatedCost,
    int IterationCount);
