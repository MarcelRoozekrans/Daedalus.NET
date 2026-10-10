using Daedalus.Application.Abstractions;
using Daedalus.Application.Commands.CreateTask;
using Daedalus.Domain.Entities;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Unit.Application.Commands;

/// <summary>
///     Unit tests for CreateTaskCommandHandler.
///     Tests command validation, task creation, repository operations, and DTO mapping.
/// </summary>
public class CreateTaskCommandHandlerTests
{
    private readonly CreateTaskCommandHandler _handler;
    private readonly ITaskRepository _taskRepository;

    public CreateTaskCommandHandlerTests()
    {
        _taskRepository = Substitute.For<ITaskRepository>();
        _handler = new CreateTaskCommandHandler(_taskRepository);
    }

    #region Success Cases

    [Fact]
    public async Task Handle_WithValidCommand_ShouldCreateTask()
    {
        // Arrange
        var command = new CreateTaskCommand(
            Guid.NewGuid(), // ProjectId
            "TASK-001", // TaskId
            "Create Feature", // Title
            "Add new feature", // Description
            Priority.High, // Priority
            "Development", // Phase
            1, // ParallelGroup
            Complexity.Medium, // Complexity
            "Implement authentication" // Prompt
        );

        var createdTask = ApplicationTestFactory.CreateTask(
            prompt: "Implement authentication");
        _taskRepository
            .AddAsync(Arg.Any<DomainTask>(), Arg.Any<CancellationToken>())
            .Returns(Result<Daedalus.Domain.Entities.Task>.Success(createdTask));

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value.Prompt.Should().Be("Implement authentication");
    }

    [Fact]
    public async Task Handle_CreatesTaskWithCorrectParameters()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var command = new CreateTaskCommand(
            projectId,
            "TASK-002",
            "Title",
            "Description",
            Priority.Medium,
            "Phase",
            2,
            Complexity.High,
            "Prompt text"
        );

        var createdTask = ApplicationTestFactory.CreateTask(
            prompt: "Prompt text"
        );
        _taskRepository
            .AddAsync(Arg.Any<DomainTask>(), Arg.Any<CancellationToken>())
            .Returns(Result<Daedalus.Domain.Entities.Task>.Success(createdTask));

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        await _taskRepository.Received(1)
            .AddAsync(Arg.Any<DomainTask>(), CancellationToken.None);
    }

    [Fact]
    public async Task Handle_MapsTaskToDto()
    {
        // Arrange
        var command = new CreateTaskCommand(
            Guid.NewGuid(),
            "TASK-003",
            "Test",
            "Test description",
            Priority.Low,
            "Testing",
            1,
            Complexity.Low,
            "Test prompt"
        );

        var createdTask = ApplicationTestFactory.CreateTask(
            prompt: "Test prompt"
        );

        _taskRepository
            .AddAsync(Arg.Any<DomainTask>(), Arg.Any<CancellationToken>())
            .Returns(Result<Daedalus.Domain.Entities.Task>.Success(createdTask));

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var dto = result.Value;
        dto.Id.Should().Be(createdTask.Id);
        dto.Prompt.Should().Be("Test prompt");
    }

    #endregion

    #region Prompt Validation

    [Fact]
    public async Task Handle_WithNullPrompt_ShouldReturnFailure()
    {
        // Arrange
        var command = new CreateTaskCommand(
            Guid.NewGuid(), "TASK-004", "Title", "Description",
            Priority.Medium, "Phase", 1, Complexity.Medium,
            null!
        );

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Prompt");
    }

    [Fact]
    public async Task Handle_WithEmptyPrompt_ShouldReturnFailure()
    {
        // Arrange
        var command = new CreateTaskCommand(
            Guid.NewGuid(), "TASK-005", "Title", "Description",
            Priority.Medium, "Phase", 1, Complexity.Medium,
            ""
        );

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Prompt");
    }

    [Fact]
    public async Task Handle_WithWhitespacePrompt_ShouldReturnFailure()
    {
        // Arrange
        var command = new CreateTaskCommand(
            Guid.NewGuid(), "TASK-006", "Title", "Description",
            Priority.Medium, "Phase", 1, Complexity.Medium,
            "   "
        );

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Prompt");
    }

    #endregion

    #region Repository Failures

    [Fact]
    public async Task Handle_RepositoryAddFails_ShouldReturnFailure()
    {
        // Arrange
        var command = new CreateTaskCommand(
            Guid.NewGuid(), "TASK-013", "Title", "Description",
            Priority.Medium, "Phase", 1, Complexity.Medium,
            "Prompt"
        );

        _taskRepository
            .AddAsync(Arg.Any<DomainTask>(), Arg.Any<CancellationToken>())
            .Returns(Result<DomainTask>.Failure("Database connection failed"));

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Database connection failed");
    }

    [Fact]
    public async Task Handle_RepositoryThrows_ShouldThrowException()
    {
        // Arrange
        var command = new CreateTaskCommand(
            Guid.NewGuid(), "TASK-014", "Title", "Description",
            Priority.Medium, "Phase", 1, Complexity.Medium,
            "Prompt"
        );

        _taskRepository
            .AddAsync(Arg.Any<DomainTask>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Repository error"));

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await _handler.Handle(command, CancellationToken.None)
        );
    }

    #endregion

    #region CancellationToken Handling

    [Fact]
    public async Task Handle_WithCancellationToken_ShouldPassToRepository()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        var command = new CreateTaskCommand(
            Guid.NewGuid(), "TASK-015", "Title", "Description",
            Priority.Medium, "Phase", 1, Complexity.Medium,
            "Prompt"
        );

        var createdTask = ApplicationTestFactory.CreateTask();
        _taskRepository
            .AddAsync(Arg.Any<DomainTask>(), Arg.Any<CancellationToken>())
            .Returns(Result<Daedalus.Domain.Entities.Task>.Success(createdTask));

        // Act
        var result = await _handler.Handle(command, cts.Token);

        // Assert
        result.IsSuccess.Should().BeTrue();
        await _taskRepository.Received(1)
            .AddAsync(Arg.Any<DomainTask>(), cts.Token);
    }

    [Fact]
    public async Task Handle_WithRepositoryException_ShouldThrowException()
    {
        // Arrange
        var command = new CreateTaskCommand(
            Guid.NewGuid(), "TASK-016", "Title", "Description",
            Priority.Medium, "Phase", 1, Complexity.Medium,
            "Prompt"
        );

        _taskRepository
            .AddAsync(Arg.Any<DomainTask>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Repository error"));

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await _handler.Handle(command, CancellationToken.None)
        );
    }

    #endregion

    #region Prompt Trimming

    [Fact]
    public async Task Handle_TrimsPromptWhitespace()
    {
        // Arrange
        var command = new CreateTaskCommand(
            Guid.NewGuid(), "TASK-017", "Title", "Description",
            Priority.Medium, "Phase", 1, Complexity.Medium,
            "  Prompt with spaces  "
        );

        DomainTask? capturedTask = null;
        _taskRepository
            .AddAsync(Arg.Do<DomainTask>(t => capturedTask = t), Arg.Any<CancellationToken>())
            .Returns(callInfo => Result<Daedalus.Domain.Entities.Task>.Success(capturedTask!));

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        capturedTask?.Prompt.Should().Be("Prompt with spaces");
    }

    [Fact]
    public async Task Handle_CreatesATaskWithoutLoopSettings()
    {
        // Arrange
        var command = new CreateTaskCommand(
            Guid.NewGuid(), "TASK-018", "Title", "Description",
            Priority.Medium, "Phase", 1, Complexity.Medium,
            "Prompt"
        );

        DomainTask? capturedTask = null;
        _taskRepository
            .AddAsync(Arg.Do<DomainTask>(t => capturedTask = t), Arg.Any<CancellationToken>())
            .Returns(callInfo => Result<Daedalus.Domain.Entities.Task>.Success(capturedTask!));

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert. Red: give Task.Create a default MaxIterations or CompletionPromise; these fail.
        result.IsSuccess.Should().BeTrue();
        capturedTask!.MaxIterations.Should().Be(0);
        capturedTask.CompletionPromise.Should().BeEmpty();
    }

    #endregion
}
