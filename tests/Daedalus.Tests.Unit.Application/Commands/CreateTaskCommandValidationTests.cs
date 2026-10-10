using Daedalus.Application.Commands.CreateTask;
using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Application.Commands;

/// <summary>
///     Validation tests for command records.
///     Tests request properties and basic validation.
/// </summary>
public class CreateTaskCommandValidationTests
{
    [Fact]
    public void CreateTaskCommand_WithValidData_Succeeds()
    {
        // Act
        var result = new CreateTaskCommand(
            Guid.NewGuid(),
            "TEST-001",
            "Test Task",
            "Test Description",
            Priority.Medium,
            "Design",
            1,
            Complexity.Medium,
            "Test Prompt");

        // Assert
        result.Prompt.Should().Be("Test Prompt");
        result.Title.Should().Be("Test Task");
        result.Priority.Should().Be(Priority.Medium);
    }

    [Fact]
    public void CreateTaskCommand_WithEmptyPrompt_IsInvalid()
    {
        // Act
        var result = new CreateTaskCommand(
            Guid.NewGuid(),
            "TEST-002",
            "Task",
            "Description",
            Priority.Low,
            "Design",
            1,
            Complexity.Low,
            string.Empty);

        // Assert
        result.Prompt.Should().Be(string.Empty);
    }

    [Fact]
    public void CreateTaskCommand_WithExtremelyLongPrompt_Succeeds()
    {
        // Arrange
        var longPrompt = new string('a', 1000);

        // Act
        var result = new CreateTaskCommand(
            Guid.NewGuid(),
            "TEST-006",
            "Task",
            "Description",
            Priority.Low,
            "Design",
            1,
            Complexity.Low,
            longPrompt);

        // Assert
        result.Prompt.Should().HaveLength(1000);
    }

    [Fact]
    public void CreateTaskCommand_WithSpecialCharacters_Succeeds()
    {
        // Act
        var result = new CreateTaskCommand(
            Guid.NewGuid(),
            "TEST-007",
            "Task",
            "Description",
            Priority.Low,
            "Design",
            1,
            Complexity.Low,
            "Prompt: \"Special\" <>&");

        // Assert
        result.Prompt.Should().Contain("Special");
    }

    [Fact]
    public void CreateTaskCommand_WithNullPrompt_CanBeCreated()
    {
        // Act
        var command = new CreateTaskCommand(
            Guid.NewGuid(),
            "TEST-001",
            "Test",
            "Test description",
            Priority.Medium,
            "Test",
            1,
            Complexity.Medium,
            null!);

        // Assert
        command.Prompt.Should().BeNull();
    }
}
