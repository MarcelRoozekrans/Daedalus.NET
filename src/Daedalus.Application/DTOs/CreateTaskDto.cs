using System.Diagnostics.CodeAnalysis;
using ZeroAlloc.Validation;

namespace Daedalus.Application.DTOs;

/// <summary>DTO for creating a task via the API.</summary>
[Validate]
public record CreateTaskDto(
    [property: NotEmpty(Message = "Project ID is required.")]
    Guid ProjectId,
    [property: MaxLength(50, Message = "Task ID cannot exceed 50 characters.")]
    string? TaskId,
    [property: Must(nameof(CreateTaskDto.IsPresent), Message = "Title is required.")]
    [property: MaxLength(500, Message = "Title cannot exceed 500 characters.")]
    string Title,
    [property: Must(nameof(CreateTaskDto.IsPresent), Message = "Description is required.")]
    [property: MaxLength(2000, Message = "Description cannot exceed 2000 characters.")]
    string Description,
    [property: InclusiveBetween(0, 4, Message = "Priority must be between 0 (Critical) and 4 (Low).")]
    int Priority,
    [property: MaxLength(100, Message = "Phase cannot exceed 100 characters.")]
    string? Phase,
    [property: GreaterThanOrEqualTo(1, Message = "Parallel group must be at least 1.")]
    int ParallelGroup,
    [property: InclusiveBetween(0, 3, Message = "Estimated complexity must be between 0 (Simple) and 3 (VeryComplex).")]
    int EstimatedComplexity,
    [property: Must(nameof(CreateTaskDto.IsPresent), Message = "Prompt is required.")]
    [property: MaxLength(8000, Message = "Prompt cannot exceed 8000 characters.")]
    string Prompt,
    [property: Must(nameof(CreateTaskDto.IsPresent), Message = "Completion promise is required.")]
    [property: MaxLength(1000, Message = "Completion promise cannot exceed 1000 characters.")]
    string CompletionPromise,
    [property: InclusiveBetween(1, 1000, Message = "Max iterations must be between 1 and 1000.")]
    int MaxIterations,
    IReadOnlyList<string>? Dependencies,
    IReadOnlyList<string>? FilesToModify)
{
    /// <summary>
    ///     Backs the <c>[Must]</c> rules above. FluentValidation's <c>NotEmpty()</c> rejects
    ///     whitespace-only strings; ZeroAlloc.Validation's <c>[NotEmpty]</c> lowers to
    ///     <c>string.IsNullOrEmpty</c>, which does not, so a plain <c>[NotEmpty]</c> here would
    ///     silently accept "   ". ProjectId (Guid) keeps <c>[NotEmpty]</c> — it is not a string.
    /// </summary>
    [SuppressMessage("Performance", "CA1822", Justification = "ZeroAlloc.Validation's [Must] attribute requires an instance method.")]
    [SuppressMessage("Major Code Smell", "S2325", Justification = "ZeroAlloc.Validation's [Must] attribute requires an instance method.")]
    internal bool IsPresent(string value) => !string.IsNullOrWhiteSpace(value);
}
