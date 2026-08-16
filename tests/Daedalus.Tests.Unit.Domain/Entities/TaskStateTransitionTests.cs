using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Domain.Entities;

/// <summary>
///     Tests for valid and invalid DomainTask state transitions.
///     Ensures the state machine is enforced correctly.
/// </summary>
public class TaskStateTransitionTests : UnitTestBase
{
    private readonly Guid _sessionId = Guid.NewGuid();

    #region Valid State Transitions

    [Fact]
    public void Task_ValidTransition_PendingToInProgress_ShouldSucceed()
    {
        // Arrange
        var task = DomainTestFactory.CreateTask(maxIterations: 10);
        task.Status.Should().Be(DomainTaskStatus.Pending);

        // Act
        var result = task.Claim(_sessionId);

        // Assert
        result.IsSuccess.Should().BeTrue();
        task.Status.Should().Be(DomainTaskStatus.InProgress);
        task.CurrentSessionId.Should().Be(_sessionId);
    }

    [Fact]
    public void Task_ValidTransition_InProgressToCompleted_ShouldSucceed()
    {
        // Arrange
        var task = DomainTestFactory.CreateTask(maxIterations: 10);
        task.Claim(_sessionId);
        task.Status.Should().Be(DomainTaskStatus.InProgress);

        // Create execution that matches completion promise
        var execution = new TaskExecution { IterationNumber = 1, LlmResponse = "DONE", CompletionPromiseFound = true };

        // Act
        var result = task.RecordExecution(execution);

        // Assert
        result.IsSuccess.Should().BeTrue();
        task.Status.Should().Be(DomainTaskStatus.Completed);
        task.Result.Should().Be("DONE");
        task.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public void Task_ValidTransition_InProgressToFailed_ShouldSucceed()
    {
        // Arrange
        var task = DomainTestFactory.CreateTask(maxIterations: 1);
        task.Claim(_sessionId);
        task.Status.Should().Be(DomainTaskStatus.InProgress);

        // Create execution that doesn't match (will hit max iterations = 1)
        var execution = new TaskExecution
        {
            IterationNumber = 1,
            LlmResponse = "Not found yet",
            CompletionPromiseFound = false
        };

        // Act
        var result = task.RecordExecution(execution);

        // Assert
        result.IsSuccess.Should().BeTrue();
        task.Status.Should().Be(DomainTaskStatus.Failed);
        task.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public void Task_ValidTransition_InProgressToAbandoned_ShouldSucceed()
    {
        // Arrange
        var task = DomainTestFactory.CreateTask(maxIterations: 10);
        task.Claim(_sessionId);
        task.Status.Should().Be(DomainTaskStatus.InProgress);

        // Act
        var result = task.Abandon();

        // Assert
        result.IsSuccess.Should().BeTrue();
        task.Status.Should().Be(DomainTaskStatus.Abandoned);
        task.CurrentSessionId.Should().BeNull();
    }

    [Fact]
    public void Task_ValidTransition_AbandonedToPending_ShouldSucceed()
    {
        // Arrange
        var task = DomainTestFactory.CreateTask(maxIterations: 10);
        task.Claim(_sessionId);
        task.Abandon();
        task.Status.Should().Be(DomainTaskStatus.Abandoned);

        // Act
        var result = task.Resume(_sessionId);

        // Assert
        result.IsSuccess.Should().BeTrue();
        task.Status.Should().Be(DomainTaskStatus.Pending);
        task.CurrentSessionId.Should().BeNull();
    }

    #endregion

    #region Invalid State Transitions

    [Fact]
    public void Task_InvalidTransition_CompletedToPending_ShouldFail()
    {
        // Arrange
        var task = DomainTestFactory.CreateTask(maxIterations: 10);
        task.Claim(_sessionId);
        var execution = new TaskExecution { IterationNumber = 1, LlmResponse = "DONE", CompletionPromiseFound = true };
        task.RecordExecution(execution);
        task.Status.Should().Be(DomainTaskStatus.Completed);

        // Act - Try to claim a completed task
        var result = task.Claim(Guid.NewGuid());

        // Assert - Should fail
        result.IsFailure.Should().BeTrue();
        result.Error.Should().ContainAny("cannot", "claim");
        // Assert
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Task_InvalidTransition_RecordExecutionOutOfSequence_ShouldFail()
    {
        // Arrange
        var task = DomainTestFactory.CreateTask(maxIterations: 1);
        task.Claim(_sessionId);

        // Act - Try to record iteration 5 when we should do 1
        var execution = new TaskExecution { IterationNumber = 5, LlmResponse = "Test", CompletionPromiseFound = false };
        var result = task.RecordExecution(execution);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().ContainAny("out of sequence", "sequence");
    }

    [Fact]
    public void Task_CannotRecordExecutionWhenNotInProgress_ShouldFail()
    {
        // Arrange
        var task = DomainTestFactory.CreateTask(completionPromise: "COMPLETE", maxIterations: 10);
        task.Status.Should().Be(DomainTaskStatus.Pending);

        var execution = new TaskExecution { IterationNumber = 1, LlmResponse = "Test", CompletionPromiseFound = false };

        // Act - Try to record without claiming first
        var result = task.RecordExecution(execution);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().ContainAny("not in progress", "not", "progress");
    }

    #endregion

    #region State Consistency

    [Fact]
    public void Task_ClaimedByMultipleSessions_SecondClaimFails()
    {
        // Arrange
        var task = DomainTestFactory.CreateTask(maxIterations: 10);
        var session1 = Guid.NewGuid();
        var session2 = Guid.NewGuid();

        // Act - Claim with session1
        task.Claim(session1);
        task.CurrentSessionId.Should().Be(session1);

        // Try to claim with session2
        var result = task.Claim(session2);

        // Assert
        result.IsFailure.Should().BeTrue();
        task.CurrentSessionId.Should().Be(session1, "Should still be owned by first session");
    }

    [Fact]
    public void Task_StatusAndSessionIdAreConsistent()
    {
        // Arrange
        var task = DomainTestFactory.CreateTask(maxIterations: 10);

        // Assert - Initial state
        task.Status.Should().Be(DomainTaskStatus.Pending);
        task.CurrentSessionId.Should().BeNull("Pending tasks should not have a session");

        // Act - Claim
        task.Claim(_sessionId);

        // Assert - After claim
        task.Status.Should().Be(DomainTaskStatus.InProgress);
        task.CurrentSessionId.Should().Be(_sessionId);

        // Act - Complete
        var execution = new TaskExecution { IterationNumber = 1, LlmResponse = "DONE", CompletionPromiseFound = true };
        task.RecordExecution(execution);

        // Assert - After completion
        task.Status.Should().Be(DomainTaskStatus.Completed);
        task.CurrentSessionId.Should().Be(_sessionId, "Session should remain after completion");
    }

    [Fact]
    public void Task_IterationCountIncrementsOnExecution()
    {
        // Arrange
        var task = DomainTestFactory.CreateTask(maxIterations: 10);
        task.IterationCount.Should().Be(0);

        // Act
        task.Claim(_sessionId);
        var exec1 = new TaskExecution { IterationNumber = 1, LlmResponse = "Try 1", CompletionPromiseFound = false };
        task.RecordExecution(exec1);

        var exec2 = new TaskExecution { IterationNumber = 2, LlmResponse = "Try 2", CompletionPromiseFound = false };
        task.RecordExecution(exec2);

        // Assert
        task.IterationCount.Should().Be(2);
    }

    [Fact]
    public void Task_MultipleExecutionsRecorded()
    {
        // Arrange
        var task = DomainTestFactory.CreateTask(maxIterations: 5);
        task.Claim(_sessionId);

        // Act
        for (var i = 1; i <= 3; i++)
        {
            var execution = new TaskExecution
            {
                IterationNumber = i,
                LlmResponse = $"Attempt {i}",
                CompletionPromiseFound = false
            };
            task.RecordExecution(execution);
        }

        // Assert
        task.Executions.Should().HaveCount(3);
        task.IterationCount.Should().Be(3);
        task.Status.Should().Be(DomainTaskStatus.InProgress);
    }

    [Fact]
    public void Task_IsNotCompletedUntilPromiseFound()
    {
        // Arrange
        var task = DomainTestFactory.CreateTask(completionPromise: "SUCCESS", maxIterations: 5);
        task.Claim(_sessionId);

        // Act - Multiple executions without finding promise
        var exec1 = new TaskExecution { IterationNumber = 1, LlmResponse = "Try 1", CompletionPromiseFound = false };
        task.RecordExecution(exec1);

        var exec2 = new TaskExecution { IterationNumber = 2, LlmResponse = "Try 2", CompletionPromiseFound = false };
        task.RecordExecution(exec2);

        // Assert - Still in progress
        task.Status.Should().Be(DomainTaskStatus.InProgress);
        task.Result.Should().BeNull();

        // Act - Find the promise
        var exec3 = new TaskExecution { IterationNumber = 3, LlmResponse = "SUCCESS", CompletionPromiseFound = true };
        task.RecordExecution(exec3);

        // Assert - Now completed
        task.Status.Should().Be(DomainTaskStatus.Completed);
        task.Result.Should().Be("SUCCESS");
    }

    #endregion
}
