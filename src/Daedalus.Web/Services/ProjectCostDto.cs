namespace Daedalus.Web.Services;

public record ProjectCostDto(
    Guid ProjectId,
    string ProjectName,
    long InputTokens,
    long OutputTokens,
    decimal EstimatedCost,
    int ExecutionCount);
