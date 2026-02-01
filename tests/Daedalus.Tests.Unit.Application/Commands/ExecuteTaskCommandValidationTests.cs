using Daedalus.Application.Commands.ExecuteTask;

namespace Daedalus.Tests.Unit.Application.Commands;

/// <summary>
///     Validation tests for ExecuteTaskCommand.
/// </summary>
public class ExecuteTaskCommandValidationTests
{
    [Fact]
    public void ExecuteTaskCommand_WithValidData_Succeeds()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        // Act
        var result = new ExecuteTaskCommand(taskId, sessionId, "worker-1");

        // Assert
        result.TaskId.Should().Be(taskId);
        result.SessionId.Should().Be(sessionId);
        result.WorkerName.Should().Be("worker-1");
    }

    [Fact]
    public void ExecuteTaskCommand_WithEmptyTaskId_IsInvalid()
    {
        // Act
        var result = new ExecuteTaskCommand(Guid.Empty, Guid.NewGuid(), "worker");

        // Assert
        result.TaskId.Should().Be(Guid.Empty);
    }

    [Fact]
    public void ExecuteTaskCommand_WithEmptySessionId_IsInvalid()
    {
        // Act
        var result = new ExecuteTaskCommand(Guid.NewGuid(), Guid.Empty, "worker");

        // Assert
        result.SessionId.Should().Be(Guid.Empty);
    }

    [Fact]
    public void ExecuteTaskCommand_WithEmptyWorkerName_IsInvalid()
    {
        // Act
        var result = new ExecuteTaskCommand(Guid.NewGuid(), Guid.NewGuid(), string.Empty);

        // Assert
        result.WorkerName.Should().Be(string.Empty);
    }

    [Fact]
    public void ExecuteTaskCommand_WithNullWorkerName_CanBeCreated()
    {
        // Act
        var command = new ExecuteTaskCommand(Guid.NewGuid(), Guid.NewGuid(), null!);

        // Assert
        command.WorkerName.Should().BeNull();
    }

    [Fact]
    public void ExecuteTaskCommand_WithLargeWorkerName_Succeeds()
    {
        // Arrange
        var largeWorkerName = new string('x', 500);

        // Act
        var result = new ExecuteTaskCommand(Guid.NewGuid(), Guid.NewGuid(), largeWorkerName);

        // Assert
        result.WorkerName.Should().HaveLength(500);
    }

    [Fact]
    public void ExecuteTaskCommand_WithSpecialCharactersInWorkerName_Succeeds()
    {
        // Act
        var result = new ExecuteTaskCommand(Guid.NewGuid(), Guid.NewGuid(), "worker-#1:special");

        // Assert
        result.WorkerName.Should().Contain("#");
    }
}
