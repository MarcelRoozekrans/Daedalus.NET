using Daedalus.Application.DTOs;
using FluentValidation;

namespace Daedalus.Application.Validators;

public sealed class SendBrainstormMessageDtoValidator : AbstractValidator<SendBrainstormMessageDto>
{
    public SendBrainstormMessageDtoValidator()
    {
        RuleFor(x => x.Content)
            .NotEmpty().WithMessage("Message content is required.")
            .MaximumLength(10000).WithMessage("Message cannot exceed 10,000 characters.");
    }
}
