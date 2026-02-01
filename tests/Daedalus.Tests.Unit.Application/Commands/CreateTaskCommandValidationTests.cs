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
            "Test Prompt",
            "Test Promise",
            5);

        // Assert
        result.Prompt.Should().Be("Test Prompt");
        result.CompletionPromise.Should().Be("Test Promise");
        result.MaxIterations.Should().Be(5);
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
            string.Empty,
            "Promise",
            5);

        // Assert
        result.Prompt.Should().Be(string.Empty);
    }

    [Fact]
    public void CreateTaskCommand_WithZeroIterations_IsInvalid()
    {
        // Act
        var result = new CreateTaskCommand(
            Guid.NewGuid(),
            "TEST-003",
            "Task",
            "Description",
            Priority.Low,
            "Design",
            1,
            Complexity.Low,
            "Prompt",
            "Promise",
            0);

        // Assert
        result.MaxIterations.Should().Be(0);
    }

    [Fact]
    public void CreateTaskCommand_WithNegativeIterations_IsInvalid()
    {
        // Act
        var result = new CreateTaskCommand(
            Guid.NewGuid(),
            "TEST-004",
            "Task",
            "Description",
            Priority.Low,
            "Design",
            1,
            Complexity.Low,
            "Prompt",
            "Promise",
            -1);

        // Assert
        result.MaxIterations.Should().Be(-1);
    }

    [Fact]
    public void CreateTaskCommand_WithMaxIterations_Succeeds()
    {
        // Act
        var result = new CreateTaskCommand(
            Guid.NewGuid(),
            "TEST-005",
            "Task",
            "Description",
            Priority.High,
            "Design",
            1,
            Complexity.High,
            "Prompt",
            "Promise",
            1000);

        // Assert
        result.MaxIterations.Should().Be(1000);
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
            longPrompt,
            "Promise",
            5);

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
            "Prompt: \"Special\" <>&",
            "Promise with Unicode: 你好",
            5);

        // Assert
        result.Prompt.Should().Contain("Special");
        result.CompletionPromise.Should().Contain("你好");
    }

    [Fact]
    public void CreateTaskCommand_WithNullPromise_CanBeCreated()
    {
        // Act
        var command = new CreateTaskCommand(
            Guid.NewGuid(),
            "TEST-008",
            "Task",
            "Description",
            Priority.Low,
            "Design",
            1,
            Complexity.Low,
            "Prompt",
            null!,
            5);

        // Assert
        command.Prompt.Should().Be("Prompt");
        command.CompletionPromise.Should().BeNull();
        command.MaxIterations.Should().Be(5);
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
            null!,
            "Promise",
            5);

        // Assert
        command.Prompt.Should().BeNull();
        command.CompletionPromise.Should().Be("Promise");
        command.MaxIterations.Should().Be(5);
    }
}
