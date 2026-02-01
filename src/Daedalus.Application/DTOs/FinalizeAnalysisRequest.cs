namespace Daedalus.Application.DTOs;

/// <summary>
///     DTO for finalizing an analysis request
/// </summary>
public record FinalizeAnalysisRequest(
    bool CreatePullRequest = true);
