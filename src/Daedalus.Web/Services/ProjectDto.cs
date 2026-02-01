namespace Daedalus.Web.Services;

public record ProjectDto(
    Guid Id,
    string ProjectName,
    string Description,
    string Version,
    DateTime CreatedAt,
    DateTime? ModifiedAt,
    IReadOnlyList<TaskDto> Tasks);
