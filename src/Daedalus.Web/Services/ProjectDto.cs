using System.Diagnostics.CodeAnalysis;

namespace Daedalus.Web.Services;

/// <summary>A project as the projects API returns it. <c>RepositoryUrl</c> is blank for a project without one.</summary>
[SuppressMessage("Design", "CA1054", Justification = "Mirrors Daedalus.Application.DTOs.ProjectDto: a remote is not always an absolute URI")]
[SuppressMessage("Design", "CA1056", Justification = "Mirrors Daedalus.Application.DTOs.ProjectDto: a remote is not always an absolute URI")]
public record ProjectDto(
    Guid Id,
    string ProjectName,
    string Description,
    string Version,
    string? RepositoryUrl,
    DateTime CreatedAt,
    DateTime? ModifiedAt,
    IReadOnlyList<TaskDto> Tasks);
