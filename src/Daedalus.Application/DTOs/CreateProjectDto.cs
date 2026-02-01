namespace Daedalus.Application.DTOs;

/// <summary>DTO for creating a new project.</summary>
public record CreateProjectDto(
    string ProjectName,
    string Description,
    string Version);
