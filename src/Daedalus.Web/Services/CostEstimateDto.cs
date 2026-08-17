namespace Daedalus.Web.Services;

public record CostEstimateDto(
    string ModelId,
    string ModelDisplayName,
    int MaxIterations,
    int EstimatedPromptTokens,
    int EstimatedResponseTokens,
    decimal EstimatedMinCost,
    decimal EstimatedMaxCost);
