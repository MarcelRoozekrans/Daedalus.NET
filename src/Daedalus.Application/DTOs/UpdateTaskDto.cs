namespace Daedalus.Application.DTOs;

/// <summary>DTO for updating a task's metadata.</summary>
public record UpdateTaskDto(
    string? Title,
    string? Description,
    int? Priority,
    string? Phase,
    int? ParallelGroup,
    int? EstimatedComplexity,
    string? Prompt,
    string? CompletionPromise,
    int? MaxIterations);
