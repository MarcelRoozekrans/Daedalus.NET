using Daedalus.Application.DTOs;
using Daedalus.Application.Validators;
using FluentValidation.TestHelper;

namespace Daedalus.Tests.Unit.Application.Validators;

public class SendBrainstormMessageDtoValidatorTests
{
    private readonly SendBrainstormMessageDtoValidator _validator = new();

    [Fact]
    public void Validate_WithValidContent_ShouldPass()
    {
        var dto = new SendBrainstormMessageDto("I need a caching layer");
        var result = _validator.TestValidate(dto);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_WithEmptyContent_ShouldFail()
    {
        var dto = new SendBrainstormMessageDto("");
        var result = _validator.TestValidate(dto);
        result.ShouldHaveValidationErrorFor(x => x.Content);
    }

    [Fact]
    public void Validate_WithTooLongContent_ShouldFail()
    {
        var dto = new SendBrainstormMessageDto(new string('x', 10001));
        var result = _validator.TestValidate(dto);
        result.ShouldHaveValidationErrorFor(x => x.Content);
    }
}
