using Daedalus.Application.DTOs;
using ZeroAlloc.Validation.Testing;

namespace Daedalus.Tests.Unit.Application.Validators;

public class SendBrainstormMessageDtoValidatorTests
{
    private readonly SendBrainstormMessageDtoValidator _validator = new();

    [Fact]
    public void Validate_WithValidContent_ShouldPass()
    {
        var dto = new SendBrainstormMessageDto("I need a caching layer");
        var result = _validator.Validate(dto);
        ValidationAssert.NoErrors(result);
    }

    [Fact]
    public void Validate_WithEmptyContent_ShouldFail()
    {
        var dto = new SendBrainstormMessageDto("");
        var result = _validator.Validate(dto);
        ValidationAssert.HasError(result, nameof(SendBrainstormMessageDto.Content));
    }

    [Fact]
    public void Validate_WithTooLongContent_ShouldFail()
    {
        var dto = new SendBrainstormMessageDto(new string('x', 10001));
        var result = _validator.Validate(dto);
        ValidationAssert.HasError(result, nameof(SendBrainstormMessageDto.Content));
    }
}
