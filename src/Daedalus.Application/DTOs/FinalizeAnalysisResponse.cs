namespace Daedalus.Application.DTOs;

#pragma warning disable CA1054 // Uri parameters should not be strings
#pragma warning disable CA1056 // Uri properties should not be strings

/// <summary>
///     DTO for finalize analysis response
/// </summary>
public record FinalizeAnalysisResponse(
    string? PullRequestUrl);
