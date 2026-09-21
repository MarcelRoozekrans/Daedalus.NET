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

    /// <summary>
    ///     Regression test for the whitespace-bypass defect: ZeroAlloc.Validation's <c>[NotEmpty]</c> lowers to
    ///     <c>string.IsNullOrEmpty</c>, which treats a whitespace-only string as present, unlike FluentValidation's
    ///     <c>NotEmpty()</c>. <c>SendBrainstormMessageDto.Content</c> now uses <c>[Must(IsPresent)]</c> backed by
    ///     <c>!string.IsNullOrWhiteSpace</c> instead. This would fail (no error reported, or more than one error
    ///     reported) if that <c>[Must]</c> rule were reverted to a plain <c>[NotEmpty]</c>, or if a length-style
    ///     attribute were added alongside it instead of replacing it.
    /// </summary>
    [Fact]
    public void Validate_WithWhitespaceOnlyContent_ShouldFailWithExactlyOneError()
    {
        var dto = new SendBrainstormMessageDto("   ");
        var result = _validator.Validate(dto);

        var contentMessages = new List<string>();
        foreach (ref readonly var failure in result.Failures)
        {
            if (string.Equals(failure.PropertyName, nameof(SendBrainstormMessageDto.Content), StringComparison.Ordinal))
            {
                contentMessages.Add(failure.ErrorMessage);
            }
        }

        contentMessages.Should().ContainSingle().Which.Should().Be("Message content is required.");
    }
}
