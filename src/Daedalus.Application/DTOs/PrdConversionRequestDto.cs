namespace Daedalus.Application.DTOs;

/// <summary>
///     Request to convert selected PRD items to tasks.
/// </summary>
public record PrdConversionRequestDto(
    Guid ProjectId,
    IReadOnlyList<PrdItemForConversionDto> PrdItems);
