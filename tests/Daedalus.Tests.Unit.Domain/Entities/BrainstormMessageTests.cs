using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Domain.Entities;

/// <summary>
///     Unit tests for BrainstormMessage value object.
/// </summary>
public class BrainstormMessageTests
{
    [Fact]
    public void Create_WithValidParameters_ShouldSucceed()
    {
        // Act
        var result = BrainstormMessage.Create(
            MessageRole.User,
            "Hello, I need help with a feature",
            BrainstormPhase.Clarification);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Role.Should().Be(MessageRole.User);
        result.Value.Content.Should().Be("Hello, I need help with a feature");
        result.Value.Phase.Should().Be(BrainstormPhase.Clarification);
        result.Value.Id.Should().NotBe(Guid.Empty);
        result.Value.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Create_WithEmptyContent_ShouldFail()
    {
        // Act
        var result = BrainstormMessage.Create(
            MessageRole.User,
            "",
            BrainstormPhase.Clarification);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Content");
    }

    [Fact]
    public void Create_WithWhitespaceContent_ShouldFail()
    {
        // Act
        var result = BrainstormMessage.Create(
            MessageRole.Assistant,
            "   ",
            BrainstormPhase.Proposals);

        // Assert
        result.IsFailure.Should().BeTrue();
    }

    [Theory]
    [InlineData(MessageRole.System)]
    [InlineData(MessageRole.Assistant)]
    [InlineData(MessageRole.User)]
    public void Create_WithAllRoles_ShouldSucceed(MessageRole role)
    {
        // Act
        var result = BrainstormMessage.Create(role, "Some content", BrainstormPhase.ContextGathering);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Role.Should().Be(role);
    }
}
