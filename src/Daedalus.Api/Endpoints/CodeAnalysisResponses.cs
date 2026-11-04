namespace Daedalus.Api.Endpoints;

/// <summary>Body of a successful <c>POST api/codeanalysis/{requestId}/iterate</c>.</summary>
/// <param name="RequestId">The analysis request that was iterated.</param>
/// <param name="Iteration">The iteration number that was recorded.</param>
public sealed record IterateAnalysisResponse(Guid RequestId, int Iteration);

/// <summary>Body of a successful <c>POST api/codeanalysis/{requestId}/cancel</c>.</summary>
/// <param name="RequestId">The analysis request that was cancelled.</param>
/// <param name="Status">Always <c>Cancelled</c>.</param>
public sealed record CancelAnalysisResponse(Guid RequestId, string Status);
