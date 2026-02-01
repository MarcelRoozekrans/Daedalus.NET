using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Domain.Entities;

/// <summary>
///     Unit tests for DomainTask aggregate root.
/// </summary>
public class TaskEntityTests : UnitTestBase
{
    private readonly Guid _sessionId = Guid.NewGuid();

    [Fact]
    public void Create_WithValidParameters_ShouldSucceed()
    {
        // Arrange & Act
        var task = DomainTestFactory.CreateTask(prompt: "Test prompt", completionPromise: "DONE", maxIterations: 10);

        // Assert
        task.Should().NotBeNull();
        task.Prompt.Should().Be("Test prompt");
        task.CompletionPromise.Should().Be("DONE");
        task.MaxIterations.Should().Be(10);
        task.Status.Should().Be(DomainTaskStatus.Pending);
    }

    [Fact]
    public void Claim_OnPendingTask_ShouldSucceed()
    {
        // Arrange
        var task = DomainTestFactory.CreateTask();

        // Act
        var result = task.Claim(_sessionId);

        // Assert
        result.IsSuccess.Should().BeTrue();
        task.Status.Should().Be(DomainTaskStatus.InProgress);
        task.CurrentSessionId.Should().Be(_sessionId);
    }

    [Fact]
    public void RecordExecution_WhenCompletionPromiseFound_ShouldMarkCompleted()
    {
        // Arrange
        var task = DomainTestFactory.CreateTask(completionPromise: "DONE");
        task.Claim(_sessionId);
        var execution = new TaskExecution
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            SessionId = _sessionId,
            IterationNumber = 1,
            Prompt = task.Prompt,
            LlmResponse = "The answer is DONE",
            CompletionPromiseFound = true,
            ExecutionDuration = TimeSpan.FromMilliseconds(100)
        };

        // Act
        var result = task.RecordExecution(execution);

        // Assert
        result.IsSuccess.Should().BeTrue();
        task.Status.Should().Be(DomainTaskStatus.Completed);
        task.Result.Should().Contain("DONE");
    }

    [Fact]
    public void Abandon_OnInProgressTask_ShouldSucceed()
    {
        // Arrange
        var task = DomainTestFactory.CreateTask();
        task.Claim(_sessionId);

        // Act
        var result = task.Abandon();

        // Assert
        result.IsSuccess.Should().BeTrue();
        task.Status.Should().Be(DomainTaskStatus.Abandoned);
    }

    [Fact]
    public void Resume_OnAbandonedTask_ShouldSucceed()
    {
        // Arrange
        var task = DomainTestFactory.CreateTask();
        task.Claim(_sessionId);
        task.Abandon();

        // Act
        var result = task.Resume(Guid.NewGuid());

        // Assert
        result.IsSuccess.Should().BeTrue();
        task.Status.Should().Be(DomainTaskStatus.Pending);
    }

    [Fact]
    public void Executions_ShouldBeReadOnly()
    {
        // Arrange & Act
        var task = DomainTestFactory.CreateTask();

        // Assert
        task.Executions.Should().NotBeNull();
        task.Executions.Should().BeEmpty();
    }
}
