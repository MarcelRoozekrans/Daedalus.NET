namespace Daedalus.Application.DTOs;

/// <summary>
///     DTO for iterating on an analysis request
/// </summary>
public record IterateAnalysisRequest(
    Guid RequestId,
    string AiResponse);
