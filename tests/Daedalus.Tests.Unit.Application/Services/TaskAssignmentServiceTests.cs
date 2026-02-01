using Daedalus.Application.Abstractions;
using Daedalus.Application.Services;
using Microsoft.Extensions.Logging;
using TaskStatus = Daedalus.Domain.Entities.TaskStatus;

namespace Daedalus.Tests.Unit.Application.Services;

/// <summary>
///     Unit tests for TaskAssignmentService.
/// </summary>
public class TaskAssignmentServiceTests : UnitTestBase
{
    private readonly ILogger<TaskAssignmentService> _logger;
    private readonly TaskAssignmentService _service;
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly ITaskRepository _taskRepository;

    public TaskAssignmentServiceTests()
    {
        _taskRepository = Substitute.For<ITaskRepository>();
        _logger = Substitute.For<ILogger<TaskAssignmentService>>();
        _service = new TaskAssignmentService(_taskRepository, _logger);
    }

    #region Integration Scenarios

    [Fact]
    public async Task ReclaimAndClaimFlow_ShouldReclaimThenAllowClaim()
    {
        // Arrange
        var oldSessionId = Guid.NewGuid();
        var newSessionId = Guid.NewGuid();

        var staleTask =
            ApplicationTestFactory.CreateTask(prompt: "Stale Task", completionPromise: "DONE", maxIterations: 10);
        staleTask.Claim(oldSessionId);

        _taskRepository
            .GetStaleInProgressAsync(Arg.Any<TimeSpan>(), _cancellationToken)
            .Returns(Result.Success((IReadOnlyList<DomainTask>)new List<DomainTask> { staleTask }));

        _taskRepository
            .UpdateAsync(Arg.Any<DomainTask>(), _cancellationToken)
            .Returns(Result.Success());

        _taskRepository
            .ClaimNextAsync(newSessionId, _cancellationToken)
            .Returns(Result.Success((DomainTask?)staleTask));

        // Act - Reclaim stale tasks
        var reclaimResult = await _service.ReclaimStaleTasksAsync(_cancellationToken);

        // Assert that task was abandoned
        reclaimResult.IsSuccess.Should().BeTrue();
        staleTask.Status.Should().Be(TaskStatus.Abandoned);

        // Now reset the task and claim it with new session
        staleTask.Resume(Guid.NewGuid());

        // Now the task should be claimable by new session
        var claimResult = await _service.GetNextAvailableTaskAsync(newSessionId, _cancellationToken);

        // Assert claim was successful
        claimResult.IsSuccess.Should().BeTrue();
    }

    #endregion

    #region GetNextAvailableTaskAsync - Success Cases

    [Fact]
    public async Task GetNextAvailableTaskAsync_WhenTaskAvailable_ShouldReturnTask()
    {
        // Arrange
        var task = ApplicationTestFactory.CreateTask(prompt: "Test prompt", completionPromise: "DONE",
            maxIterations: 10);
        _taskRepository
            .ClaimNextAsync(_sessionId, _cancellationToken)
            .Returns(Result.Success((DomainTask?)task));

        // Act
        var result = await _service.GetNextAvailableTaskAsync(_sessionId, _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Id.Should().Be(task.Id);
        result.Value.Prompt.Should().Be(task.Prompt);

        await _taskRepository.Received(1).ClaimNextAsync(_sessionId, _cancellationToken);
    }

    [Fact]
    public async Task GetNextAvailableTaskAsync_WhenNoTaskAvailable_ShouldReturnNull()
    {
        // Arrange
        _taskRepository
            .ClaimNextAsync(_sessionId, _cancellationToken)
            .Returns(Result.Success((DomainTask?)null));

        // Act
        var result = await _service.GetNextAvailableTaskAsync(_sessionId, _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task GetNextAvailableTaskAsync_WithDifferentSessions_ShouldClaimCorrectSession()
    {
        // Arrange
        var session1 = Guid.NewGuid();
        var session2 = Guid.NewGuid();
        var task = ApplicationTestFactory.CreateTask(prompt: "Test", completionPromise: "DONE", maxIterations: 10);

        _taskRepository
            .ClaimNextAsync(session1, _cancellationToken)
            .Returns(Result.Success((DomainTask?)task));

        // Act
        var result1 = await _service.GetNextAvailableTaskAsync(session1, _cancellationToken);
        var result2 = await _service.GetNextAvailableTaskAsync(session2, _cancellationToken);

        // Assert
        result1.IsSuccess.Should().BeTrue();
        await _taskRepository.Received(1).ClaimNextAsync(session1, _cancellationToken);
        await _taskRepository.Received(1).ClaimNextAsync(session2, _cancellationToken);
    }

    #endregion

    #region GetNextAvailableTaskAsync - Failure Cases

    [Fact]
    public async Task GetNextAvailableTaskAsync_WhenRepositoryFails_ShouldReturnFailure()
    {
        // Arrange
        const string errorMessage = "Database connection failed";
        _taskRepository
            .ClaimNextAsync(_sessionId, _cancellationToken)
            .Returns(Result.Failure<DomainTask?>(errorMessage));

        // Act
        var result = await _service.GetNextAvailableTaskAsync(_sessionId, _cancellationToken);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(errorMessage);
    }

    [Fact]
    public async Task GetNextAvailableTaskAsync_MultipleCallsToSameSession_ShouldClaimDifferentTasks()
    {
        // Arrange
        var task1 = ApplicationTestFactory.CreateTask(prompt: "Task 1", completionPromise: "DONE", maxIterations: 10);
        var task2 = ApplicationTestFactory.CreateTask(prompt: "Task 2", completionPromise: "DONE", maxIterations: 10);

        _taskRepository
            .ClaimNextAsync(_sessionId, _cancellationToken)
            .Returns(Result.Success((DomainTask?)task1), Result.Success((DomainTask?)task2));

        // Act
        var result1 = await _service.GetNextAvailableTaskAsync(_sessionId, _cancellationToken);
        var result2 = await _service.GetNextAvailableTaskAsync(_sessionId, _cancellationToken);

        // Assert
        result1.IsSuccess.Should().BeTrue();
        result1.Value!.Id.Should().Be(task1.Id);
        result2.IsSuccess.Should().BeTrue();
        result2.Value!.Id.Should().Be(task2.Id);
    }

    #endregion

    #region ReclaimStaleTasksAsync - Success Cases

    [Fact]
    public async Task ReclaimStaleTasksAsync_WithNoStaleTasks_ShouldReturnSuccess()
    {
        // Arrange
        _taskRepository
            .GetStaleInProgressAsync(Arg.Any<TimeSpan>(), _cancellationToken)
            .Returns(Result.Success((IReadOnlyList<DomainTask>)new List<DomainTask>()));

        // Act
        var result = await _service.ReclaimStaleTasksAsync(_cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        await _taskRepository.Received(1).GetStaleInProgressAsync(Arg.Any<TimeSpan>(), _cancellationToken);
    }

    [Fact]
    public async Task ReclaimStaleTasksAsync_WithStaleTasks_ShouldAbandonAndUpdate()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var task1 = ApplicationTestFactory.CreateTask(prompt: "Stale Task 1", completionPromise: "DONE",
            maxIterations: 10);
        task1.Claim(sessionId);

        var task2 = ApplicationTestFactory.CreateTask(prompt: "Stale Task 2", completionPromise: "DONE",
            maxIterations: 10);
        task2.Claim(sessionId);

        var staleTasks = new List<DomainTask> { task1, task2 };

        _taskRepository
            .GetStaleInProgressAsync(Arg.Any<TimeSpan>(), _cancellationToken)
            .Returns(Result.Success((IReadOnlyList<DomainTask>)staleTasks));

        _taskRepository
            .UpdateAsync(Arg.Any<DomainTask>(), _cancellationToken)
            .Returns(Result.Success());

        // Act
        var result = await _service.ReclaimStaleTasksAsync(_cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        task1.Status.Should().Be(TaskStatus.Abandoned);
        task2.Status.Should().Be(TaskStatus.Abandoned);
        await _taskRepository.Received(2).UpdateAsync(Arg.Any<DomainTask>(), _cancellationToken);
    }

    [Fact]
    public async Task ReclaimStaleTasksAsync_WithSingleStaleTask_ShouldReclaim()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var task = ApplicationTestFactory.CreateTask(prompt: "Stale Task", completionPromise: "DONE",
            maxIterations: 10);
        task.Claim(sessionId);

        var staleTasks = new List<DomainTask> { task };

        _taskRepository
            .GetStaleInProgressAsync(Arg.Any<TimeSpan>(), _cancellationToken)
            .Returns(Result.Success((IReadOnlyList<DomainTask>)staleTasks));

        _taskRepository
            .UpdateAsync(Arg.Any<DomainTask>(), _cancellationToken)
            .Returns(Result.Success());

        // Act
        var result = await _service.ReclaimStaleTasksAsync(_cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        task.Status.Should().Be(TaskStatus.Abandoned);
        task.CurrentSessionId.Should().BeNull();
    }

    #endregion

    #region ReclaimStaleTasksAsync - Failure Cases

    [Fact]
    public async Task ReclaimStaleTasksAsync_WhenGetStaleFails_ShouldReturnFailure()
    {
        // Arrange
        const string errorMessage = "Failed to query stale tasks";
        _taskRepository
            .GetStaleInProgressAsync(Arg.Any<TimeSpan>(), _cancellationToken)
            .Returns(Result.Failure<IReadOnlyList<DomainTask>>(errorMessage));

        // Act
        var result = await _service.ReclaimStaleTasksAsync(_cancellationToken);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(errorMessage);
    }

    [Fact]
    public async Task ReclaimStaleTasksAsync_WhenUpdateFails_ShouldContinueWithNextTask()
    {
        // Arrange
        var sessionId1 = Guid.NewGuid();
        var sessionId2 = Guid.NewGuid();

        var task1 = ApplicationTestFactory.CreateTask(prompt: "Stale Task 1", completionPromise: "DONE",
            maxIterations: 10);
        task1.Claim(sessionId1);

        var task2 = ApplicationTestFactory.CreateTask(prompt: "Stale Task 2", completionPromise: "DONE",
            maxIterations: 10);
        task2.Claim(sessionId2);

        var staleTasks = new List<DomainTask> { task1, task2 };

        _taskRepository
            .GetStaleInProgressAsync(Arg.Any<TimeSpan>(), _cancellationToken)
            .Returns(Result.Success((IReadOnlyList<DomainTask>)staleTasks));

        _taskRepository
            .UpdateAsync(Arg.Any<DomainTask>(), _cancellationToken)
            .Returns(Result.Failure("Update failed for task 1"), Result.Success());

        // Act
        var result = await _service.ReclaimStaleTasksAsync(_cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        task1.Status.Should().Be(TaskStatus.Abandoned);
        task2.Status.Should().Be(TaskStatus.Abandoned);
        await _taskRepository.Received(2).UpdateAsync(Arg.Any<DomainTask>(), _cancellationToken);
    }

    #endregion

    #region ReclaimStaleTasksAsync - Edge Cases

    [Fact]
    public async Task ReclaimStaleTasksAsync_WithLargeNumberOfStaleTasks()
    {
        // Arrange
        var staleTasks = new List<DomainTask>();
        for (var i = 0; i < 100; i++)
        {
            var task = ApplicationTestFactory.CreateTask(prompt: $"Task {i}", completionPromise: "DONE",
                maxIterations: 10);
            task.Claim(Guid.NewGuid());
            staleTasks.Add(task);
        }

        _taskRepository
            .GetStaleInProgressAsync(Arg.Any<TimeSpan>(), _cancellationToken)
            .Returns(Result.Success((IReadOnlyList<DomainTask>)staleTasks));

        _taskRepository
            .UpdateAsync(Arg.Any<DomainTask>(), _cancellationToken)
            .Returns(Result.Success());

        // Act
        var result = await _service.ReclaimStaleTasksAsync(_cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        foreach (var task in staleTasks)
        {
            task.Status.Should().Be(TaskStatus.Abandoned);
        }

        await _taskRepository.Received(100).UpdateAsync(Arg.Any<DomainTask>(), _cancellationToken);
    }

    [Fact]
    public async Task ReclaimStaleTasksAsync_UsesCorrectStalenessTimeout()
    {
        // Arrange
        _taskRepository
            .GetStaleInProgressAsync(Arg.Is<TimeSpan>(t => t == TimeSpan.FromMinutes(5)),
                _cancellationToken)
            .Returns(Result.Success((IReadOnlyList<DomainTask>)new List<DomainTask>()));

        // Act
        var result = await _service.ReclaimStaleTasksAsync(_cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        await _taskRepository.Received(1)
            .GetStaleInProgressAsync(Arg.Is<TimeSpan>(t => t == TimeSpan.FromMinutes(5)), _cancellationToken);
    }

    #endregion
}
