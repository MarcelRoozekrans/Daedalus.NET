using System.Diagnostics.CodeAnalysis;

namespace Daedalus.Application.DTOs;

/// <summary>DTO for creating a new project.</summary>
[SuppressMessage("Design", "CA1054", Justification = "DTO uses string for JSON serialization")]
[SuppressMessage("Design", "CA1056", Justification = "DTO uses string for JSON serialization")]
public record CreateProjectDto(
    string ProjectName,
    string Description,
    string Version,
    string? RepositoryUrl = null,
    string? DefaultBranch = null);
