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

    #region Prompt and ParallelGroup defaults

    [Fact]
    public async Task A_task_without_a_prompt_takes_its_description()
    {
        var captured = await HandleAndCapture(null, 1);

        // Red: drop the description fallback in the handler; the stored prompt is then empty and Create fails.
        captured.Prompt.Should().Be("Description of the work");
    }

    [Fact]
    public async Task A_blank_prompt_counts_as_missing()
    {
        var captured = await HandleAndCapture("   ", 1);

        // Red: test only for null in the handler; the blank prompt is then refused and capture stays null.
        captured.Prompt.Should().Be("Description of the work");
    }

    [Fact]
    public async Task A_given_prompt_wins_over_the_description()
    {
        var captured = await HandleAndCapture("Explicit prompt", 1);

        // Red: always use the description in the handler; the prompt then reads "Description of the work".
        captured.Prompt.Should().Be("Explicit prompt");
    }

    [Fact]
    public async Task A_task_without_a_parallel_group_takes_one()
    {
        var captured = await HandleAndCapture("Prompt", null);

        // Red: default a missing group to 0 in the handler; the group is then 0 and Create refuses or this reads 0.
        captured.ParallelGroup.Should().Be(1);
    }

    [Fact]
    public async Task A_given_parallel_group_is_kept()
    {
        var captured = await HandleAndCapture("Prompt", 3);

        // Red: always use 1 in the handler; the group then reads 1.
        captured.ParallelGroup.Should().Be(3);
    }

    private async Task<DomainTask> HandleAndCapture(string? prompt, int? parallelGroup)
    {
        var command = new CreateTaskCommand(
            Guid.NewGuid(), "TASK-004", "Title", "Description of the work",
            Priority.Medium, "Phase", parallelGroup, Complexity.Medium,
            prompt);

        DomainTask? capturedTask = null;
        _taskRepository
            .AddAsync(Arg.Do<DomainTask>(t => capturedTask = t), Arg.Any<CancellationToken>())
            .Returns(_ => Result<DomainTask>.Success(capturedTask!));

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error : null);
        return capturedTask!;
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
