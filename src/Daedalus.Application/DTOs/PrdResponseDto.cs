namespace Daedalus.Application.DTOs;

/// <summary>
///     Represents a complete PRD response from the LLM.
/// </summary>
public record PrdResponseDto(
    string Id,
    Guid ProjectId,
    string ProductOverview,
    IReadOnlyList<string> Goals,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<PrdItemDto> Features,
    IReadOnlyList<PrdItemDto> Tasks,
    DateTime GeneratedAt,
    string? RawResponse = null)
{
    /// <summary>
    ///     Gets all PRD items (combined features and tasks).
    /// </summary>
    public IReadOnlyList<PrdItemDto> AllItems =>
        (Features ?? [])
        .Concat(Tasks ?? [])
        .ToList()
        .AsReadOnly();
}
