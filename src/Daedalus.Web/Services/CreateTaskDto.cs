namespace Daedalus.Web.Services;

/// <summary>DTO for creating a task via the API.</summary>
public record CreateTaskDto(
    Guid ProjectId,
    string? TaskId,
    string Title,
    string Description,
    int Priority,
    string? Phase,
    int ParallelGroup,
    int EstimatedComplexity,
    string Prompt,
    string CompletionPromise,
    int MaxIterations,
    IReadOnlyList<string>? Dependencies,
    IReadOnlyList<string>? FilesToModify);
