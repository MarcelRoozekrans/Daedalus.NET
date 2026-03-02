using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Domain.Entities;

/// <summary>
///     Unit tests for BrainstormMessage child entity.
/// </summary>
public class BrainstormMessageTests
{
    private static readonly Guid SessionId = Guid.NewGuid();

    [Fact]
    public void Create_WithValidParameters_ShouldSucceed()
    {
        // Act
        var result = BrainstormMessage.Create(
            SessionId,
            MessageRole.User,
            "Hello, I need help with a feature",
            BrainstormPhase.Clarification);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.BrainstormSessionId.Should().Be(SessionId);
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
            SessionId,
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
            SessionId,
            MessageRole.Assistant,
            "   ",
            BrainstormPhase.Proposals);

        // Assert
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Create_WithEmptySessionId_ShouldFail()
    {
        // Act
        var result = BrainstormMessage.Create(
            Guid.Empty,
            MessageRole.User,
            "Some content",
            BrainstormPhase.Clarification);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("BrainstormSessionId");
    }

    [Theory]
    [InlineData(MessageRole.System)]
    [InlineData(MessageRole.Assistant)]
    [InlineData(MessageRole.User)]
    public void Create_WithAllRoles_ShouldSucceed(MessageRole role)
    {
        // Act
        var result = BrainstormMessage.Create(SessionId, role, "Some content", BrainstormPhase.ContextGathering);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Role.Should().Be(role);
    }
}
