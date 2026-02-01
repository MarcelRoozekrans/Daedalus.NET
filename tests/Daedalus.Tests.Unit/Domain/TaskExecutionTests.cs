#pragma warning disable CA1707

using Daedalus.Domain.Entities;
using Daedalus.Tests.Unit.Abstractions;

namespace Daedalus.Tests.Unit.Domain;

/// <summary>
///     Unit tests for TaskExecution value object.
/// </summary>
public class TaskExecutionTests : UnitTestBase
{
    [Fact]
    public void Create_WithValidData_ShouldInitializeProperties()
    {
        // Arrange
        var executionId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        const string prompt = "Test prompt";
        const string response = "Test response";
        var duration = TimeSpan.FromMilliseconds(100);

        // Act
        var execution = new TaskExecution
        {
            Id = executionId,
            TaskId = taskId,
            SessionId = sessionId,
            IterationNumber = 1,
            Prompt = prompt,
            LlmResponse = response,
            CompletionPromiseFound = true,
            ExecutionDuration = duration
        };

        // Assert
        execution.Id.Should().Be(executionId);
        execution.TaskId.Should().Be(taskId);
        execution.SessionId.Should().Be(sessionId);
        execution.IterationNumber.Should().Be(1);
        execution.Prompt.Should().Be(prompt);
        execution.LlmResponse.Should().Be(response);
        execution.CompletionPromiseFound.Should().BeTrue();
        execution.ExecutionDuration.Should().Be(duration);
    }

    [Fact]
    public void ExecutedAt_ShouldDefaultToUtcNow()
    {
        // Act
        var execution = new TaskExecution();

        // Assert
        execution.ExecutedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
        execution.ExecutedAt.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void Error_CanBeNull()
    {
        // Act
        var execution = new TaskExecution();

        // Assert
        execution.Error.Should().BeNull();
    }

    [Fact]
    public void Error_CanContainErrorMessage()
    {
        // Arrange
        const string errorMessage = "Connection timeout";

        // Act
        var execution = new TaskExecution { Error = errorMessage };

        // Assert
        execution.Error.Should().Be(errorMessage);
    }

    [Fact]
    public void CompletionPromiseFound_DefaultsToFalse()
    {
        // Act
        var execution = new TaskExecution();

        // Assert
        execution.CompletionPromiseFound.Should().BeFalse();
    }

    [Fact]
    public void LlmResponse_CanBeEmptyString()
    {
        // Arrange
        var execution = new TaskExecution { LlmResponse = string.Empty };

        // Assert
        execution.LlmResponse.Should().Be(string.Empty);
    }

    [Fact]
    public void LlmResponse_CanBeLongString()
    {
        // Arrange
        var longResponse = string.Concat(Enumerable.Repeat("Generated code ", 1000));
        var execution = new TaskExecution { LlmResponse = longResponse };

        // Assert
        execution.LlmResponse.Should().Be(longResponse);
        execution.LlmResponse.Length.Should().Be(longResponse.Length);
    }

    [Fact]
    public void ExecutionDuration_CanBeZero()
    {
        // Act
        var execution = new TaskExecution { ExecutionDuration = TimeSpan.Zero };

        // Assert
        execution.ExecutionDuration.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void IterationNumber_CanBeAnyInteger()
    {
        // Act & Assert
        var execution1 = new TaskExecution { IterationNumber = 0 };
        execution1.IterationNumber.Should().Be(0);

        var execution2 = new TaskExecution { IterationNumber = 1 };
        execution2.IterationNumber.Should().Be(1);

        var execution3 = new TaskExecution { IterationNumber = 1000 };
        execution3.IterationNumber.Should().Be(1000);
    }

    [Fact]
    public void MultipleExecutions_ShouldHaveDifferentIds()
    {
        // Act
        var execution1 = new TaskExecution { Id = Guid.NewGuid() };
        var execution2 = new TaskExecution { Id = Guid.NewGuid() };
        var execution3 = new TaskExecution { Id = Guid.NewGuid() };

        // Assert
        execution1.Id.Should().NotBe(execution2.Id);
        execution2.Id.Should().NotBe(execution3.Id);
        execution1.Id.Should().NotBe(execution3.Id);
    }

    [Fact]
    public void Execution_TrackingMultipleIterationsOfSameTask()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        // Act
        var execution1 = new TaskExecution
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            SessionId = sessionId,
            IterationNumber = 1,
            Prompt = "Test",
            LlmResponse = "Response 1",
            CompletionPromiseFound = false
        };

        var execution2 = new TaskExecution
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            SessionId = sessionId,
            IterationNumber = 2,
            Prompt = "Test",
            LlmResponse = "Response 2",
            CompletionPromiseFound = false
        };

        // Assert
        execution1.TaskId.Should().Be(execution2.TaskId);
        execution1.SessionId.Should().Be(execution2.SessionId);
        execution1.IterationNumber.Should().BeLessThan(execution2.IterationNumber);
    }
}
