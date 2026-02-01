using Daedalus.Application.Abstractions;
using Daedalus.Application.Queries.GetTaskById;
using Daedalus.Domain.Entities;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Unit.Application.Queries;

/// <summary>
///     Tests for GetTaskByIdQueryHandler.
/// </summary>
public class GetTaskByIdQueryHandlerTests
{
    private readonly GetTaskByIdQueryHandler _handler;
    private readonly ITaskRepository _taskRepository;

    public GetTaskByIdQueryHandlerTests()
    {
        _taskRepository = Substitute.For<ITaskRepository>();
        _handler = new GetTaskByIdQueryHandler(_taskRepository);
    }

    [Fact]
    public async Task Handle_WithValidTaskId_ShouldReturnTaskDto()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var query = new GetTaskByIdQuery(taskId);
        var domainTask = ApplicationTestFactory.CreateTask(taskId);

        _taskRepository
            .GetByIdAsync(taskId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(domainTask));

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value.Id.Should().Be(taskId);
        result.Value.Prompt.Should().Be("Test prompt");
    }

    [Fact]
    public async Task Handle_WithTaskNotFound_ShouldReturnFailure()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var query = new GetTaskByIdQuery(taskId);

        _taskRepository
            .GetByIdAsync(taskId, Arg.Any<CancellationToken>())
            .Returns(Result.Failure<DomainTask>("Task not found"));

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Task not found");
    }

    [Fact]
    public async Task Handle_WithEmptyTaskId_ShouldReturnFailure()
    {
        // Arrange
        var query = new GetTaskByIdQuery(Guid.Empty);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("TaskId cannot be empty");
    }

    [Fact]
    public async Task Handle_MapsDomainTaskToDto()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var query = new GetTaskByIdQuery(taskId);
        var domainTask = ApplicationTestFactory.CreateTask(
            taskId,
            title: "My Task",
            description: "Task description",
            prompt: "Generate code",
            completionPromise: "CODE_GENERATED"
        );

        _taskRepository
            .GetByIdAsync(taskId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(domainTask));

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Prompt.Should().Be("Generate code");
        result.Value.CompletionPromise.Should().Be("CODE_GENERATED");
    }

    [Fact]
    public async Task Handle_PassesCancellationTokenToRepository()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var query = new GetTaskByIdQuery(taskId);
        var cts = new CancellationTokenSource();
        var domainTask = ApplicationTestFactory.CreateTask(taskId);

        _taskRepository
            .GetByIdAsync(taskId, cts.Token)
            .Returns(Result.Success(domainTask));

        // Act
        var result = await _handler.Handle(query, cts.Token);

        // Assert
        result.IsSuccess.Should().BeTrue();
        await _taskRepository.Received(1)
            .GetByIdAsync(taskId, cts.Token);
    }

    [Fact]
    public async Task Handle_IncludesTaskExecutions_InDto()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var query = new GetTaskByIdQuery(taskId);
        var domainTask = ApplicationTestFactory.CreateTask(taskId);
        var sessionId = Guid.NewGuid();

        domainTask.Claim(sessionId);

        var execution = new TaskExecution
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            SessionId = sessionId,
            IterationNumber = 1,
            Prompt = "Test prompt",
            LlmResponse = "Test response",
            CompletionPromiseFound = false,
            ExecutedAt = DateTime.UtcNow,
            ExecutionDuration = TimeSpan.FromSeconds(5)
        };
        domainTask.RecordExecution(execution);

        _taskRepository
            .GetByIdAsync(taskId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(domainTask));

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Executions.Should().NotBeEmpty();
        result.Value.Executions.Should().HaveCount(1);
        result.Value.Executions[0].IterationNumber.Should().Be(1);
    }

    [Fact]
    public async Task Handle_WithRepositoryException_ShouldReturnFailure()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var query = new GetTaskByIdQuery(taskId);

        _taskRepository
            .GetByIdAsync(taskId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Database error"));

        // Act
        var act = async () => await _handler.Handle(query, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Handle_WithRepositoryFailure_PropagatesError()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var query = new GetTaskByIdQuery(taskId);
        var errorMessage = "Database connection failed";

        _taskRepository
            .GetByIdAsync(taskId, Arg.Any<CancellationToken>())
            .Returns(Result.Failure<DomainTask>(errorMessage));

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(errorMessage);
    }

    [Fact]
    public async Task Handle_WithMultipleExecutions_MapsAllToDto()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var query = new GetTaskByIdQuery(taskId);
        var domainTask = ApplicationTestFactory.CreateTask(taskId);
        var sessionId = Guid.NewGuid();

        domainTask.Claim(sessionId);

        for (var i = 1; i <= 3; i++)
        {
            var execution = new TaskExecution
            {
                Id = Guid.NewGuid(),
                TaskId = taskId,
                SessionId = sessionId,
                IterationNumber = i,
                Prompt = $"Prompt {i}",
                LlmResponse = $"Response {i}",
                CompletionPromiseFound = i == 3,
                ExecutedAt = DateTime.UtcNow,
                ExecutionDuration = TimeSpan.FromSeconds(i)
            };
            domainTask.RecordExecution(execution);
        }

        _taskRepository
            .GetByIdAsync(taskId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(domainTask));

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Executions.Should().HaveCount(3);
        result.Value.Executions[0].IterationNumber.Should().Be(1);
        result.Value.Executions[1].IterationNumber.Should().Be(2);
        result.Value.Executions[2].IterationNumber.Should().Be(3);
        result.Value.Executions[2].CompletionPromiseFound.Should().BeTrue();
    }
}
