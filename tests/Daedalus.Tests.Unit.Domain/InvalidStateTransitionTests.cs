using Daedalus.Domain.Entities;
using TaskStatusEntity = Daedalus.Domain.Entities.TaskStatus;

namespace Daedalus.Tests.Unit.Domain;

/// <summary>
///     Tests for invalid state transitions in domain entities.
///     Tests business rule validation and state machine constraints.
/// </summary>
public class InvalidStateTransitionTests
{
    [Fact]
    public void Task_CanRecordExecutionWhenClaimed()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var task = DomainTestFactory.CreateTask(taskId, prompt: "Test Task", completionPromise: "Promise",
            maxIterations: 5);
        var sessionId = Guid.NewGuid();

        // Must claim task first
        task.Claim(sessionId);

        // Act - Record execution
        var execution1 = new TaskExecution
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            SessionId = sessionId,
            IterationNumber = 1,
            Prompt = "prompt-1",
            LlmResponse = "response-1",
            CompletionPromiseFound = false,
            ExecutionDuration = TimeSpan.FromSeconds(1)
        };

        var result1 = task.RecordExecution(execution1);

        // Assert
        result1.IsSuccess.Should().BeTrue();
        task.IterationCount.Should().Be(1);
        task.Executions.Should().HaveCount(1);
    }

    [Fact]
    public void Task_CannotClaimAfterAlreadyClaimed()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var task = DomainTestFactory.CreateTask(taskId, prompt: "Test Task", completionPromise: "Promise",
            maxIterations: 5);
        var session1 = Guid.NewGuid();
        var session2 = Guid.NewGuid();

        // Act
        var claim1 = task.Claim(session1);
        var claim2 = task.Claim(session2);

        // Assert
        claim1.IsSuccess.Should().BeTrue();
        claim2.IsSuccess.Should().BeFalse("Cannot claim already-in-progress task");
        task.CurrentSessionId.Should().Be(session1);
    }

    [Fact]
    public void Task_CanOnlyAbandonInProgress()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var task = DomainTestFactory.CreateTask(taskId, prompt: "Test Task", completionPromise: "Promise",
            maxIterations: 5);

        // Act - Try to abandon pending task
        var abandonResult = task.Abandon();

        // Assert
        abandonResult.IsSuccess.Should().BeFalse("Cannot abandon pending task");
        task.Status.Should().Be(TaskStatusEntity.Pending);
    }

    [Fact]
    public void Task_CompletionPromiseFoundMarksComplete()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var task = DomainTestFactory.CreateTask(taskId, prompt: "Test Task", completionPromise: "found it",
            maxIterations: 5);
        var sessionId = Guid.NewGuid();

        task.Claim(sessionId);

        // Act - Record execution with completion promise found
        var execution = new TaskExecution
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            SessionId = sessionId,
            IterationNumber = 1,
            Prompt = "test",
            LlmResponse = "here is found it in response",
            CompletionPromiseFound = true,
            ExecutionDuration = TimeSpan.FromSeconds(1)
        };

        var result = task.RecordExecution(execution);

        // Assert
        result.IsSuccess.Should().BeTrue();
        task.Status.Should().Be(TaskStatusEntity.Completed);
        task.Result.Should().Be("here is found it in response");
    }

    [Fact]
    public void Task_MaxIterationsMarksFailed()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var task = DomainTestFactory.CreateTask(taskId, prompt: "Test Task", completionPromise: "Promise",
            maxIterations: 2); // Max 2 iterations
        var sessionId = Guid.NewGuid();

        task.Claim(sessionId);

        // Act - Record max iterations without finding promise
        var execution1 = new TaskExecution
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            SessionId = sessionId,
            IterationNumber = 1,
            Prompt = "test",
            LlmResponse = "response-1",
            CompletionPromiseFound = false,
            ExecutionDuration = TimeSpan.FromSeconds(1)
        };
        task.RecordExecution(execution1);

        var execution2 = new TaskExecution
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            SessionId = sessionId,
            IterationNumber = 2,
            Prompt = "test",
            LlmResponse = "response-2",
            CompletionPromiseFound = false,
            ExecutionDuration = TimeSpan.FromSeconds(1)
        };
        task.RecordExecution(execution2);

        // Assert
        task.Status.Should().Be(TaskStatusEntity.Failed);
        task.IterationCount.Should().Be(2);
    }

    [Fact]
    public void ExecutionSession_CanBeShutdown()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var session = ExecutionSession.Create(sessionId, "test-worker").Value;

        // Act
        session.Shutdown();

        // Assert
        session.IsActive.Should().BeFalse();
    }

    [Fact]
    public void ExecutionSession_ShutdownCanOnlyHappenOnce()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var session = ExecutionSession.Create(sessionId, "test-worker").Value;

        // Act
        session.Shutdown();
        var afterFirstShutdown = session.IsActive;

        session.Shutdown(); // Second shutdown
        var afterSecondShutdown = session.IsActive;

        // Assert
        afterFirstShutdown.Should().BeFalse();
        afterSecondShutdown.Should().BeFalse();
    }

    [Fact]
    public void Task_Resume_CanOnlyApplyToAbandoned()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var task = DomainTestFactory.CreateTask(taskId, prompt: "Test Task", completionPromise: "Promise",
            maxIterations: 5);
        var newSessionId = Guid.NewGuid();

        // Act - Try to resume pending task
        var resumeResult = task.Resume(newSessionId);

        // Assert
        resumeResult.IsSuccess.Should().BeFalse("Cannot resume pending task");
    }

    [Fact]
    public void Task_AbandonedCanBeResumed()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var task = DomainTestFactory.CreateTask(taskId, prompt: "Test Task", completionPromise: "Promise",
            maxIterations: 5);
        var sessionId = Guid.NewGuid();
        var newSessionId = Guid.NewGuid();

        task.Claim(sessionId);
        task.Abandon();

        // Act
        var resumeResult = task.Resume(newSessionId);

        // Assert
        resumeResult.IsSuccess.Should().BeTrue();
        task.Status.Should().Be(TaskStatusEntity.Pending);
    }

    [Fact]
    public void ExecutionSession_WorkerNameIsImmutable()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var session = ExecutionSession.Create(sessionId, "original-worker").Value;

        // Act
        var originalName = session.WorkerName;

        // Assert
        originalName.Should().Be("original-worker");
    }
}
