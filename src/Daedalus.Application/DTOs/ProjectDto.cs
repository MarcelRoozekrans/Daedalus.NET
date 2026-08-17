using System.Diagnostics.CodeAnalysis;

namespace Daedalus.Application.DTOs;

/// <summary>DTO for Project entity.</summary>
[SuppressMessage("Design", "CA1054", Justification = "DTO uses string for JSON serialization")]
[SuppressMessage("Design", "CA1056", Justification = "DTO uses string for JSON serialization")]
public record ProjectDto(
    Guid Id,
    string ProjectName,
    string Description,
    string Version,
    string RepositoryUrl,
    string DefaultBranch,
    DateTime CreatedAt,
    DateTime? ModifiedAt,
    IReadOnlyList<TaskDto> Tasks);
