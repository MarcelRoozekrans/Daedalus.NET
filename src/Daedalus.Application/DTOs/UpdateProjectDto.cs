using ZeroAlloc.Validation;

namespace Daedalus.Application.DTOs;

/// <summary>DTO for updating an existing project.</summary>
[Validate]
public record UpdateProjectDto(
    [property: NotEmpty(Message = "Project name is required.")]
    [property: MaxLength(256, Message = "Project name cannot exceed 256 characters.")]
    string ProjectName,
    [property: NotEmpty(Message = "Description is required.")]
    [property: MaxLength(2000, Message = "Description cannot exceed 2000 characters.")]
    string Description,
    [property: NotEmpty(Message = "Version is required.")]
    [property: MaxLength(50, Message = "Version cannot exceed 50 characters.")]
    string Version);
