namespace Daedalus.Application.DTOs;

/// <summary>
///     Represents a user's requirements for PRD generation.
/// </summary>
public record PrdRequestDto(
    Guid ProjectId,
    string UserRequirements,
    string? Context = null,
    int? MaxItems = 10)
{
    /// <summary>
    ///     Initializes a new instance of the PrdRequestDto record.
    /// </summary>
    public PrdRequestDto() : this(Guid.Empty, string.Empty)
    {
    }
}
