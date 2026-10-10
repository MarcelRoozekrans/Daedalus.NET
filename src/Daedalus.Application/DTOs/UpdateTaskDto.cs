using ZeroAlloc.Validation;

namespace Daedalus.Application.DTOs;

/// <summary>DTO for updating a task's metadata.</summary>
[Validate]
public record UpdateTaskDto(
    [property: MaxLength(500, Message = "Title cannot exceed 500 characters.")]
    string? Title,
    [property: MaxLength(2000, Message = "Description cannot exceed 2000 characters.")]
    string? Description,
    [property: InclusiveBetween(0, 4, Message = "Priority must be between 0 (Critical) and 4 (Low).", When = nameof(UpdateTaskDto.PriorityHasValue))]
    int? Priority,
    [property: MaxLength(100, Message = "Phase cannot exceed 100 characters.")]
    string? Phase,
    [property: GreaterThanOrEqualTo(1, Message = "Parallel group must be at least 1.", When = nameof(UpdateTaskDto.ParallelGroupHasValue))]
    int? ParallelGroup,
    [property: InclusiveBetween(0, 3, Message = "Estimated complexity must be between 0 (Simple) and 3 (VeryComplex).", When = nameof(UpdateTaskDto.EstimatedComplexityHasValue))]
    int? EstimatedComplexity,
    [property: MaxLength(8000, Message = "Prompt cannot exceed 8000 characters.")]
    string? Prompt)
{
    /// <summary>Guards <see cref="Priority"/>'s range check so an omitted value is not coerced to 0 and checked.</summary>
    internal bool PriorityHasValue() => Priority.HasValue;

    /// <summary>Guards <see cref="ParallelGroup"/>'s range check so an omitted value is not coerced to 0 and checked.</summary>
    internal bool ParallelGroupHasValue() => ParallelGroup.HasValue;

    /// <summary>Guards <see cref="EstimatedComplexity"/>'s range check so an omitted value is not coerced to 0 and checked.</summary>
    internal bool EstimatedComplexityHasValue() => EstimatedComplexity.HasValue;

}
