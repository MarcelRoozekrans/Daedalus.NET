using Daedalus.Application.DTOs;
using FluentValidation;

namespace Daedalus.Application.Validators;

/// <summary>
///     Validates CreateTaskDto before it reaches the command handler.
/// </summary>
public sealed class CreateTaskDtoValidator : AbstractValidator<CreateTaskDto>
{
    public CreateTaskDtoValidator()
    {
        RuleFor(x => x.ProjectId)
            .NotEmpty().WithMessage("Project ID is required.");

        RuleFor(x => x.Title)
            .NotEmpty().WithMessage("Title is required.")
            .MaximumLength(500).WithMessage("Title cannot exceed 500 characters.");

        RuleFor(x => x.Description)
            .NotEmpty().WithMessage("Description is required.")
            .MaximumLength(2000).WithMessage("Description cannot exceed 2000 characters.");

        RuleFor(x => x.Prompt)
            .NotEmpty().WithMessage("Prompt is required.")
            .MaximumLength(8000).WithMessage("Prompt cannot exceed 8000 characters.");

        RuleFor(x => x.CompletionPromise)
            .NotEmpty().WithMessage("Completion promise is required.")
            .MaximumLength(1000).WithMessage("Completion promise cannot exceed 1000 characters.");

        RuleFor(x => x.MaxIterations)
            .InclusiveBetween(1, 1000).WithMessage("Max iterations must be between 1 and 1000.");

        RuleFor(x => x.ParallelGroup)
            .GreaterThanOrEqualTo(1).WithMessage("Parallel group must be at least 1.");

        RuleFor(x => x.Priority)
            .InclusiveBetween(0, 4).WithMessage("Priority must be between 0 (Critical) and 4 (Low).");

        RuleFor(x => x.EstimatedComplexity)
            .InclusiveBetween(0, 3).WithMessage("Estimated complexity must be between 0 (Simple) and 3 (VeryComplex).");

        RuleFor(x => x.TaskId)
            .MaximumLength(50).WithMessage("Task ID cannot exceed 50 characters.")
            .When(x => x.TaskId is not null);

        RuleFor(x => x.Phase)
            .MaximumLength(100).WithMessage("Phase cannot exceed 100 characters.")
            .When(x => x.Phase is not null);
    }
}
