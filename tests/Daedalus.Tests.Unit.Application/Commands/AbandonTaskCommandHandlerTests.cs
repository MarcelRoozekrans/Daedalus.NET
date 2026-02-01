using Daedalus.Application.Abstractions;
using Daedalus.Application.Commands.AbandonTask;
using Daedalus.Domain.Entities;
using Task = System.Threading.Tasks.Task;
using TaskStatus = Daedalus.Domain.Entities.TaskStatus;

namespace Daedalus.Tests.Unit.Application.Commands;

/// <summary>
///     Tests for AbandonTaskCommandHandler.
/// </summary>
public class AbandonTaskCommandHandlerTests
{
    private readonly AbandonTaskCommandHandler _handler;
    private readonly ITaskRepository _taskRepository;

    public AbandonTaskCommandHandlerTests()
    {
        _taskRepository = Substitute.For<ITaskRepository>();
        _handler = new AbandonTaskCommandHandler(_taskRepository);
    }

    [Fact]
    public async Task Handle_WithValidInProgressTask_ShouldAbandonTask()
    {
        // Arrange - Tasks must be InProgress to be abandoned
        var taskId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var command = new AbandonTaskCommand(taskId, "User requested cancellation");
        var task = ApplicationTestFactory.CreateTask(taskId);
        task.Claim(sessionId); // Move to InProgress

        _taskRepository
            .GetByIdAsync(taskId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(task));

        _taskRepository
            .UpdateAsync(Arg.Any<DomainTask>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value.Id.Should().Be(taskId);
        task.Status.Should().Be(TaskStatus.Abandoned);
    }

    [Fact]
    public async Task Handle_WithEmptyTaskId_ShouldReturnFailure()
    {
        // Arrange
        var command = new AbandonTaskCommand(Guid.Empty, "Some reason");

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("TaskId cannot be empty");
    }

    [Fact]
    public async Task Handle_WithEmptyReason_ShouldReturnFailure()
    {
        // Arrange
        var command = new AbandonTaskCommand(Guid.NewGuid(), string.Empty);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Reason cannot be empty");
    }

    [Fact]
    public async Task Handle_WithNullReason_ShouldReturnFailure()
    {
        // Arrange
        var command = new AbandonTaskCommand(Guid.NewGuid(), null!);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Reason cannot be empty");
    }

    [Fact]
    public async Task Handle_WithWhitespaceReason_ShouldReturnFailure()
    {
        // Arrange
        var command = new AbandonTaskCommand(Guid.NewGuid(), "   ");

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Reason cannot be empty");
    }

    [Fact]
    public async Task Handle_WithTaskNotFound_ShouldReturnFailure()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var command = new AbandonTaskCommand(taskId, "Reason");

        _taskRepository
            .GetByIdAsync(taskId, Arg.Any<CancellationToken>())
            .Returns(Result.Failure<DomainTask>("Task not found"));

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Task not found");
    }

    [Fact]
    public async Task Handle_WithCompletedTask_ShouldReturnFailure()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var command = new AbandonTaskCommand(taskId, "Try to abandon completed task");
        var task = ApplicationTestFactory.CreateTask(taskId);

        // Mark task as completed by recording execution with completion promise found
        var execution = new TaskExecution
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            SessionId = Guid.NewGuid(),
            IterationNumber = 1,
            Prompt = "Test",
            LlmResponse = task.CompletionPromise,
            CompletionPromiseFound = true,
            ExecutedAt = DateTime.UtcNow,
            ExecutionDuration = TimeSpan.Zero
        };
        task.Claim(execution.SessionId);
        task.RecordExecution(execution);

        _taskRepository
            .GetByIdAsync(taskId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(task));

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("cannot be abandoned");
    }

    [Fact]
    public async Task Handle_WithAbandonedTask_ShouldReturnFailure()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var command = new AbandonTaskCommand(taskId, "Try to abandon already abandoned task");
        var task = ApplicationTestFactory.CreateTask(taskId);
        task.Claim(sessionId);
        task.Abandon();

        _taskRepository
            .GetByIdAsync(taskId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(task));

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("cannot be abandoned");
    }

    [Fact]
    public async Task Handle_CallsRepositoryUpdateAsync()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var command = new AbandonTaskCommand(taskId, "Test reason");
        var task = ApplicationTestFactory.CreateTask(taskId);
        task.Claim(sessionId);

        _taskRepository
            .GetByIdAsync(taskId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(task));

        _taskRepository
            .UpdateAsync(Arg.Any<DomainTask>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        await _taskRepository.Received(1)
            .UpdateAsync(Arg.Any<DomainTask>(), CancellationToken.None);
    }

    [Fact]
    public async Task Handle_WithRepositoryUpdateFailure_ShouldReturnFailure()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var command = new AbandonTaskCommand(taskId, "Reason");
        var task = ApplicationTestFactory.CreateTask(taskId);
        task.Claim(sessionId);

        _taskRepository
            .GetByIdAsync(taskId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(task));

        _taskRepository
            .UpdateAsync(Arg.Any<DomainTask>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Database error"));

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Failed to update task");
    }

    [Fact]
    public async Task Handle_PassesCancellationTokenToRepository()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var command = new AbandonTaskCommand(taskId, "Reason");
        var task = ApplicationTestFactory.CreateTask(taskId);
        task.Claim(sessionId);
        var cts = new CancellationTokenSource();

        _taskRepository
            .GetByIdAsync(taskId, cts.Token)
            .Returns(Result.Success(task));

        _taskRepository
            .UpdateAsync(Arg.Any<DomainTask>(), cts.Token)
            .Returns(Result.Success());

        // Act
        var result = await _handler.Handle(command, cts.Token);

        // Assert
        result.IsSuccess.Should().BeTrue();
        await _taskRepository.Received(1)
            .GetByIdAsync(taskId, cts.Token);
    }

    [Fact]
    public async Task Handle_ReturnsMappedTaskDto()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var command = new AbandonTaskCommand(taskId, "Reason");
        var task = ApplicationTestFactory.CreateTask(
            taskId,
            prompt: "Generate report",
            completionPromise: "REPORT_GENERATED"
        );
        task.Claim(sessionId);

        _taskRepository
            .GetByIdAsync(taskId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(task));

        _taskRepository
            .UpdateAsync(Arg.Any<DomainTask>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be(taskId);
        result.Value.Prompt.Should().Be("Generate report");
        result.Value.CompletionPromise.Should().Be("REPORT_GENERATED");
    }

    [Fact]
    public async Task Handle_WithPendingTask_ShouldReturnFailure()
    {
        // Arrange - Pending tasks cannot be abandoned directly; only InProgress tasks can
        var taskId = Guid.NewGuid();
        var command = new AbandonTaskCommand(taskId, "User cancelled pending task");
        var task = ApplicationTestFactory.CreateTask(taskId);

        // Verify task is pending
        task.Status.Should().Be(TaskStatus.Pending);

        _taskRepository
            .GetByIdAsync(taskId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(task));

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Only in-progress tasks can be abandoned");
    }

    [Fact]
    public async Task Handle_WithRepositoryException_ShouldThrow()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var command = new AbandonTaskCommand(taskId, "Reason");

        _taskRepository
            .GetByIdAsync(taskId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Database connection failed"));

        // Act
        var act = async () => await _handler.Handle(command, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Handle_WithLongReason_ShouldSucceed()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var longReason = new string('x', 1000);
        var command = new AbandonTaskCommand(taskId, longReason);
        var task = ApplicationTestFactory.CreateTask(taskId);
        task.Claim(sessionId);

        _taskRepository
            .GetByIdAsync(taskId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(task));

        _taskRepository
            .UpdateAsync(Arg.Any<DomainTask>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }
}
