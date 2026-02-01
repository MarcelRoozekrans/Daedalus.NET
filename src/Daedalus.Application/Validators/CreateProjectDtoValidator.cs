using Daedalus.Application.DTOs;
using FluentValidation;

namespace Daedalus.Application.Validators;

/// <summary>
///     Validates CreateProjectDto before it reaches the command handler.
/// </summary>
public sealed class CreateProjectDtoValidator : AbstractValidator<CreateProjectDto>
{
    public CreateProjectDtoValidator()
    {
        RuleFor(x => x.ProjectName)
            .NotEmpty().WithMessage("Project name is required.")
            .MaximumLength(256).WithMessage("Project name cannot exceed 256 characters.");

        RuleFor(x => x.Description)
            .NotEmpty().WithMessage("Description is required.")
            .MaximumLength(2000).WithMessage("Description cannot exceed 2000 characters.");

        RuleFor(x => x.Version)
            .NotEmpty().WithMessage("Version is required.")
            .MaximumLength(50).WithMessage("Version cannot exceed 50 characters.");
    }
}
