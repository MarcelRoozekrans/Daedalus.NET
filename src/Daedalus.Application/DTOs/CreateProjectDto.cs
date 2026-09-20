using System.Diagnostics.CodeAnalysis;
using ZeroAlloc.Validation;

namespace Daedalus.Application.DTOs;

/// <summary>DTO for creating a new project.</summary>
[SuppressMessage("Design", "CA1054", Justification = "DTO uses string for JSON serialization")]
[SuppressMessage("Design", "CA1056", Justification = "DTO uses string for JSON serialization")]
[Validate]
public record CreateProjectDto(
    [property: NotEmpty(Message = "Project name is required.")]
    [property: MaxLength(256, Message = "Project name cannot exceed 256 characters.")]
    string ProjectName,
    [property: NotEmpty(Message = "Description is required.")]
    [property: MaxLength(2000, Message = "Description cannot exceed 2000 characters.")]
    string Description,
    [property: NotEmpty(Message = "Version is required.")]
    [property: MaxLength(50, Message = "Version cannot exceed 50 characters.")]
    string Version,
    string? RepositoryUrl = null,
    string? DefaultBranch = null);
