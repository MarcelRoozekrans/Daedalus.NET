namespace Daedalus.Application.DTOs;

/// <summary>
///     DTO for analysis iteration details
/// </summary>
public record AnalysisIterationDto(
    int IterationNumber,
    string Prompt,
    string AiResponse,
    bool CompilationSucceeded,
    DateTime CreatedAt,
    DateTime? CompletedAt);
