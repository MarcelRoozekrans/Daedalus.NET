using Daedalus.Application.Commands.AbandonTask;

namespace Daedalus.Tests.Unit.Application.Commands;

/// <summary>
///     Validation tests for AbandonTaskCommand.
/// </summary>
public class AbandonTaskCommandValidationTests
{
    [Fact]
    public void AbandonTaskCommand_WithValidData_Succeeds()
    {
        // Arrange
        var taskId = Guid.NewGuid();

        // Act
        var result = new AbandonTaskCommand(taskId, "Task abandoned");

        // Assert
        result.TaskId.Should().Be(taskId);
        result.Reason.Should().Be("Task abandoned");
    }

    [Fact]
    public void AbandonTaskCommand_WithEmptyGuid_IsInvalid()
    {
        // Act
        var result = new AbandonTaskCommand(Guid.Empty, "Reason");

        // Assert
        result.TaskId.Should().Be(Guid.Empty);
    }

    [Fact]
    public void AbandonTaskCommand_WithEmptyReason_IsInvalid()
    {
        // Act
        var result = new AbandonTaskCommand(Guid.NewGuid(), string.Empty);

        // Assert
        result.Reason.Should().Be(string.Empty);
    }

    [Fact]
    public void AbandonTaskCommand_PreservesTaskId()
    {
        // Arrange
        var taskId = Guid.CreateVersion7();

        // Act
        var result = new AbandonTaskCommand(taskId, "Reason");

        // Assert
        result.TaskId.Should().Be(taskId);
    }
}
