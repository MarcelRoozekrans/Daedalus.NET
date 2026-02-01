#pragma warning disable CA1054 // Uri parameters should not be strings

using Daedalus.Application.DTOs;
using FluentValidation;

namespace Daedalus.Application.Validators;

/// <summary>
///     Validates SubmitAnalysisRequest before it reaches the code analysis controller.
/// </summary>
public sealed class SubmitAnalysisRequestValidator : AbstractValidator<SubmitAnalysisRequest>
{
    public SubmitAnalysisRequestValidator()
    {
        RuleFor(x => x.Title)
            .NotEmpty().WithMessage("Title is required.")
            .MaximumLength(256).WithMessage("Title cannot exceed 256 characters.");

        RuleFor(x => x.Description)
            .NotEmpty().WithMessage("Description is required.")
            .MaximumLength(2000).WithMessage("Description cannot exceed 2000 characters.");

        RuleFor(x => x.RepositoryUrl)
            .NotEmpty().WithMessage("Repository URL is required.")
            .MaximumLength(1024).WithMessage("Repository URL cannot exceed 1024 characters.");

        RuleFor(x => x.FilePath)
            .NotEmpty().WithMessage("File path is required.")
            .MaximumLength(1024).WithMessage("File path cannot exceed 1024 characters.");

        RuleFor(x => x.MaxIterations)
            .InclusiveBetween(1, 100).WithMessage("Max iterations must be between 1 and 100.");

        RuleFor(x => x.Type)
            .IsInEnum().WithMessage("Invalid analysis type.");
    }
}
