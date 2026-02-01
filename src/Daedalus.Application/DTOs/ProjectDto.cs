namespace Daedalus.Application.DTOs;

/// <summary>DTO for Project entity.</summary>
public record ProjectDto(
    Guid Id,
    string ProjectName,
    string Description,
    string Version,
    DateTime CreatedAt,
    DateTime? ModifiedAt,
    IReadOnlyList<TaskDto> Tasks);
