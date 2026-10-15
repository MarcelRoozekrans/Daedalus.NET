namespace Daedalus.Web.Services;

/// <summary>
///     DTO for creating a task via the API. Only the project, title and description are required: a null prompt makes the
///     API use the description, and a null phase, parallel group, dependency list or file list takes the API's default.
/// </summary>
public record CreateTaskDto(
    Guid ProjectId,
    string? TaskId,
    string Title,
    string Description,
    int Priority,
    string? Phase,
    int? ParallelGroup,
    int EstimatedComplexity,
    string? Prompt,
    IReadOnlyList<string>? Dependencies,
    IReadOnlyList<string>? FilesToModify);
