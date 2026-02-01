using Daedalus.Application.DTOs;
using FluentValidation;

namespace Daedalus.Application.Validators;

/// <summary>
///     Validates UpdateTaskDto before it reaches the command handler.
/// </summary>
public sealed class UpdateTaskDtoValidator : AbstractValidator<UpdateTaskDto>
{
    public UpdateTaskDtoValidator()
    {
        RuleFor(x => x.Title)
            .MaximumLength(500).WithMessage("Title cannot exceed 500 characters.")
            .When(x => x.Title is not null);

        RuleFor(x => x.Description)
            .MaximumLength(2000).WithMessage("Description cannot exceed 2000 characters.")
            .When(x => x.Description is not null);

        RuleFor(x => x.Prompt)
            .MaximumLength(8000).WithMessage("Prompt cannot exceed 8000 characters.")
            .When(x => x.Prompt is not null);

        RuleFor(x => x.CompletionPromise)
            .MaximumLength(1000).WithMessage("Completion promise cannot exceed 1000 characters.")
            .When(x => x.CompletionPromise is not null);

        RuleFor(x => x.MaxIterations)
            .InclusiveBetween(1, 1000).WithMessage("Max iterations must be between 1 and 1000.")
            .When(x => x.MaxIterations.HasValue);

        RuleFor(x => x.ParallelGroup)
            .GreaterThanOrEqualTo(1).WithMessage("Parallel group must be at least 1.")
            .When(x => x.ParallelGroup.HasValue);

        RuleFor(x => x.Priority)
            .InclusiveBetween(0, 4).WithMessage("Priority must be between 0 (Critical) and 4 (Low).")
            .When(x => x.Priority.HasValue);

        RuleFor(x => x.EstimatedComplexity)
            .InclusiveBetween(0, 3).WithMessage("Estimated complexity must be between 0 (Simple) and 3 (VeryComplex).")
            .When(x => x.EstimatedComplexity.HasValue);

        RuleFor(x => x.Phase)
            .MaximumLength(100).WithMessage("Phase cannot exceed 100 characters.")
            .When(x => x.Phase is not null);
    }
}
