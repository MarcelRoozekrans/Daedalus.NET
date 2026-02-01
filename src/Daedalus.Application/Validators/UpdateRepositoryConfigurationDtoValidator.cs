#pragma warning disable CA1054 // Uri parameters should not be strings

using Daedalus.Application.DTOs;
using FluentValidation;

namespace Daedalus.Application.Validators;

/// <summary>
///     Validates UpdateRepositoryConfigurationDto before it reaches the repository controller.
/// </summary>
public sealed class UpdateRepositoryConfigurationDtoValidator : AbstractValidator<UpdateRepositoryConfigurationDto>
{
    public UpdateRepositoryConfigurationDtoValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Repository name is required.")
            .MaximumLength(256).WithMessage("Repository name cannot exceed 256 characters.");

        RuleFor(x => x.Url)
            .NotEmpty().WithMessage("Repository URL is required.")
            .MaximumLength(1024).WithMessage("Repository URL cannot exceed 1024 characters.");

        RuleFor(x => x.DefaultBranch)
            .NotEmpty().WithMessage("Default branch is required.")
            .MaximumLength(256).WithMessage("Default branch cannot exceed 256 characters.");

        RuleFor(x => x.AuthenticationMethod)
            .MaximumLength(100).WithMessage("Authentication method cannot exceed 100 characters.")
            .When(x => x.AuthenticationMethod is not null);

        RuleFor(x => x.CredentialIdentifier)
            .MaximumLength(512).WithMessage("Credential identifier cannot exceed 512 characters.")
            .When(x => x.CredentialIdentifier is not null);

        RuleFor(x => x.Description)
            .MaximumLength(2000).WithMessage("Description cannot exceed 2000 characters.")
            .When(x => x.Description is not null);
    }
}
