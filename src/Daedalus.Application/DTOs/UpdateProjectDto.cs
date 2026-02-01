namespace Daedalus.Application.DTOs;

/// <summary>DTO for updating an existing project.</summary>
public record UpdateProjectDto(
    string ProjectName,
    string Description,
    string Version);
