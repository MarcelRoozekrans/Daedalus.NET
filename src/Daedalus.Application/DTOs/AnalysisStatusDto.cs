namespace Daedalus.Application.DTOs;

/// <summary>
///     DTO for analysis status response
/// </summary>
public record AnalysisStatusDto(
    CodeAnalysisRequestDto Request,
    string CurrentPrompt,
    IReadOnlyList<AnalysisIterationDto> Iterations);
