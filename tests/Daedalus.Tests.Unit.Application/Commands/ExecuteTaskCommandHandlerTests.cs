using Daedalus.Application.Abstractions;
using Daedalus.Application.Commands.ExecuteTask;
using Daedalus.Domain.Entities;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Unit.Application.Commands;

/// <summary>
///     Tests for ExecuteTaskCommandHandler.
/// </summary>
public class ExecuteTaskCommandHandlerTests
{
    private readonly ExecuteTaskCommandHandler _handler;
    private readonly IRalphAgentFactory _agentFactory;
    private readonly ITaskRepository _taskRepository;

    public ExecuteTaskCommandHandlerTests()
    {
        _agentFactory = Substitute.For<IRalphAgentFactory>();
        _taskRepository = Substitute.For<ITaskRepository>();
        _handler = new ExecuteTaskCommandHandler(_taskRepository, _agentFactory);
    }

    [Fact]
    public async Task Handle_WithValidCommand_ShouldExecuteTask()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var command = new ExecuteTaskCommand(taskId, sessionId, "worker1");
        var task = ApplicationTestFactory.CreateTask(taskId, maxIterations: 5);

        _taskRepository
            .GetByIdAsync(taskId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(task));

        _agentFactory
            .InvokeAsync(task.Prompt, Arg.Any<CancellationToken>())
            .Returns(Result.Success("LLM response without completion promise"));

        _taskRepository
            .UpdateAsync(Arg.Any<DomainTask>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Task.Id.Should().Be(taskId);
        result.Value.IsCompleted.Should().BeFalse();
        result.Value.Execution.IterationNumber.Should().Be(1);
    }

    [Fact]
    public async Task Handle_WithEmptyTaskId_ShouldReturnFailure()
    {
        // Arrange
        var command = new ExecuteTaskCommand(Guid.Empty, Guid.NewGuid(), "worker1");

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("TaskId cannot be empty");
    }

    [Fact]
    public async Task Handle_WithEmptySessionId_ShouldReturnFailure()
    {
        // Arrange
        var command = new ExecuteTaskCommand(Guid.NewGuid(), Guid.Empty, "worker1");

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("SessionId cannot be empty");
    }

    [Fact]
    public async Task Handle_WithEmptyWorkerName_ShouldReturnFailure()
    {
        // Arrange
        var command = new ExecuteTaskCommand(Guid.NewGuid(), Guid.NewGuid(), string.Empty);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("WorkerName cannot be empty");
    }

    [Fact]
    public async Task Handle_WithTaskNotFound_ShouldReturnFailure()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var command = new ExecuteTaskCommand(taskId, Guid.NewGuid(), "worker1");

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
        var sessionId = Guid.NewGuid();
        var command = new ExecuteTaskCommand(taskId, sessionId, "worker1");
        var task = ApplicationTestFactory.CreateTask(taskId);
        task.Claim(sessionId); // Must claim before recording execution

        // Mark task as completed
        var execution = new TaskExecution
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            SessionId = sessionId,
            IterationNumber = 1,
            Prompt = "Test",
            LlmResponse = task.CompletionPromise,
            CompletionPromiseFound = true,
            ExecutedAt = DateTime.UtcNow,
            ExecutionDuration = TimeSpan.Zero
        };
        task.RecordExecution(execution);

        _taskRepository
            .GetByIdAsync(taskId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(task));

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("cannot be executed");
    }

    [Fact]
    public async Task Handle_WithMaxIterationsReached_ShouldReturnFailure()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var command = new ExecuteTaskCommand(taskId, sessionId, "worker1");
        var task = ApplicationTestFactory.CreateTask(taskId, maxIterations: 1);
        task.Claim(sessionId); // Must claim before recording execution

        // Use up all iterations - this will set status to Failed
        var execution = new TaskExecution
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            SessionId = sessionId,
            IterationNumber = 1,
            Prompt = "Test",
            LlmResponse = "response",
            CompletionPromiseFound = false, // Not completed, so status becomes Failed at max iterations
            ExecutedAt = DateTime.UtcNow,
            ExecutionDuration = TimeSpan.Zero
        };
        task.RecordExecution(execution);
        // At this point, task.Status should be Failed since IterationCount (1) >= MaxIterations (1) and no completion promise

        _taskRepository
            .GetByIdAsync(taskId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(task));

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("cannot be executed");
    }

    [Fact]
    public async Task Handle_WithLlmFailure_ShouldReturnFailure()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var command = new ExecuteTaskCommand(taskId, sessionId, "worker1");
        var task = ApplicationTestFactory.CreateTask(taskId);

        _taskRepository
            .GetByIdAsync(taskId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(task));

        _agentFactory
            .InvokeAsync(task.Prompt, Arg.Any<CancellationToken>())
            .Returns(Result.Failure<string>("LLM service unavailable"));

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("LLM invocation failed");
    }

    [Fact]
    public async Task Handle_DetectsCompletionPromise()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var command = new ExecuteTaskCommand(taskId, sessionId, "worker1");
        var task = ApplicationTestFactory.CreateTask(taskId, completionPromise: "TASK_COMPLETE");

        _taskRepository
            .GetByIdAsync(taskId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(task));

        _agentFactory
            .InvokeAsync(task.Prompt, Arg.Any<CancellationToken>())
            .Returns(Result.Success("Work done. TASK_COMPLETE"));

        _taskRepository
            .UpdateAsync(Arg.Any<DomainTask>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.IsCompleted.Should().BeTrue();
        result.Value.Execution.CompletionPromiseFound.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_CompletionPromiseIsCaseInsensitive()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var command = new ExecuteTaskCommand(taskId, sessionId, "worker1");
        var task = ApplicationTestFactory.CreateTask(taskId, completionPromise: "COMPLETE");

        _taskRepository
            .GetByIdAsync(taskId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(task));

        _agentFactory
            .InvokeAsync(task.Prompt, Arg.Any<CancellationToken>())
            .Returns(Result.Success("Work done. complete")); // lowercase

        _taskRepository
            .UpdateAsync(Arg.Any<DomainTask>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_WithRepositoryUpdateFailure_ShouldReturnFailure()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var command = new ExecuteTaskCommand(taskId, sessionId, "worker1");
        var task = ApplicationTestFactory.CreateTask(taskId);

        _taskRepository
            .GetByIdAsync(taskId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(task));

        _agentFactory
            .InvokeAsync(task.Prompt, Arg.Any<CancellationToken>())
            .Returns(Result.Success("response"));

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
    public async Task Handle_IncreasesIterationCount()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var command = new ExecuteTaskCommand(taskId, sessionId, "worker1");
        var task = ApplicationTestFactory.CreateTask(taskId, maxIterations: 10);
        var initialIterationCount = task.IterationCount;

        _taskRepository
            .GetByIdAsync(taskId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(task));

        _agentFactory
            .InvokeAsync(task.Prompt, Arg.Any<CancellationToken>())
            .Returns(Result.Success("response"));

        _taskRepository
            .UpdateAsync(Arg.Any<DomainTask>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Execution.IterationNumber.Should().Be(initialIterationCount + 1);
    }

    [Fact]
    public async Task Handle_PassesCancellationTokenToServices()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var command = new ExecuteTaskCommand(taskId, sessionId, "worker1");
        var task = ApplicationTestFactory.CreateTask(taskId);
        var cts = new CancellationTokenSource();

        _taskRepository
            .GetByIdAsync(taskId, cts.Token)
            .Returns(Result.Success(task));

        _agentFactory
            .InvokeAsync(task.Prompt, cts.Token)
            .Returns(Result.Success("response"));

        _taskRepository
            .UpdateAsync(Arg.Any<DomainTask>(), cts.Token)
            .Returns(Result.Success());

        // Act
        var result = await _handler.Handle(command, cts.Token);

        // Assert
        result.IsSuccess.Should().BeTrue();
        await _agentFactory.Received(1).InvokeAsync(task.Prompt, cts.Token);
    }

    [Fact]
    public async Task Handle_ReturnsMappedExecutionData()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var command = new ExecuteTaskCommand(taskId, sessionId, "worker1");
        var task = ApplicationTestFactory.CreateTask(taskId, prompt: "Generate", completionPromise: "DONE");

        _taskRepository
            .GetByIdAsync(taskId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(task));

        _agentFactory
            .InvokeAsync("Generate", Arg.Any<CancellationToken>())
            .Returns(Result.Success("Output: DONE"));

        _taskRepository
            .UpdateAsync(Arg.Any<DomainTask>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Execution.Prompt.Should().Be("Generate");
        result.Value.Execution.LlmResponse.Should().Be("Output: DONE");
        result.Value.Execution.CompletionPromiseFound.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_WithMultipleIterations_TracksProgressCorrectly()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var command = new ExecuteTaskCommand(taskId, sessionId, "worker1");
        var task = ApplicationTestFactory.CreateTask(taskId, maxIterations: 5);
        task.Claim(sessionId); // Must claim before recording execution

        // Add one prior execution
        var priorExecution = new TaskExecution
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            SessionId = sessionId,
            IterationNumber = 1,
            Prompt = task.Prompt,
            LlmResponse = "First attempt",
            CompletionPromiseFound = false,
            ExecutedAt = DateTime.UtcNow,
            ExecutionDuration = TimeSpan.Zero
        };
        task.RecordExecution(priorExecution);

        _taskRepository
            .GetByIdAsync(taskId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(task));

        _agentFactory
            .InvokeAsync(task.Prompt, Arg.Any<CancellationToken>())
            .Returns(Result.Success("Second attempt"));

        _taskRepository
            .UpdateAsync(Arg.Any<DomainTask>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Execution.IterationNumber.Should().Be(2);
    }
}
