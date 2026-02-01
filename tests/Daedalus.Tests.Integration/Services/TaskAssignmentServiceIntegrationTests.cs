using Daedalus.Application.Services;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SystemTask = System.Threading.Tasks.Task;
using TaskStatus = Daedalus.Domain.Entities.TaskStatus;

namespace Daedalus.Tests.Integration.Services;

/// <summary>
///     Integration tests for TaskAssignmentService with real database.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class TaskAssignmentServiceIntegrationTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly ILogger<TaskAssignmentService> _mockLogger = Substitute.For<ILogger<TaskAssignmentService>>();

    private readonly ILogger<ExecutionSessionRepository> _sessionLoggerMock =
        Substitute.For<ILogger<ExecutionSessionRepository>>();

    private readonly ILogger<TaskRepository> _taskLoggerMock = Substitute.For<ILogger<TaskRepository>>();
    private ApplicationDbContext _dbContext = null!;
    private TaskAssignmentService _service = null!;
    private ExecutionSessionRepository _sessionRepository = null!;
    private TaskRepository _taskRepository = null!;

    public async SystemTask InitializeAsync()
    {
        await fixture.DatabaseResetter.ResetAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;

        _dbContext = new ApplicationDbContext(options);
        _taskRepository = new TaskRepository(_dbContext, _taskLoggerMock);
        _sessionRepository = new ExecutionSessionRepository(_dbContext, _sessionLoggerMock);
        _service = new TaskAssignmentService(_taskRepository, _mockLogger);

        // Create default project for tests
        var project = IntegrationTestFactory.CreateProject(IntegrationTestFactory.GetDefaultProjectId());
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync();
    }

    public async SystemTask DisposeAsync()
    {
        if (_dbContext != null)
        {
            await _dbContext.DisposeAsync();
        }
    }

    #region GetNextAvailableTaskAsync Integration Tests

    [Fact(Timeout = 5000)]
    public async SystemTask GetNextAvailableTaskAsync_WithMultiplePendingTasks_ShouldClaimFirstByCreatedDate()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var session = ExecutionSession.Create(sessionId, "worker-001").Value;
        await _sessionRepository.AddAsync(session, CancellationToken.None);

        var task1 = IntegrationTestFactory.CreateTask(prompt: "First task", completionPromise: "DONE",
            maxIterations: 10);
        var task2 = IntegrationTestFactory.CreateTask(prompt: "Second task", completionPromise: "DONE",
            maxIterations: 10);
        var task3 = IntegrationTestFactory.CreateTask(prompt: "Third task", completionPromise: "DONE",
            maxIterations: 10);

        await _taskRepository.AddAsync(task1, CancellationToken.None);
        await SystemTask.Delay(10);
        await _taskRepository.AddAsync(task2, CancellationToken.None);
        await SystemTask.Delay(10);
        await _taskRepository.AddAsync(task3, CancellationToken.None);

        // Act
        var result = await _service.GetNextAvailableTaskAsync(sessionId, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Id.Should().Be(task1.Id);
        result.Value.Status.Should().Be(TaskStatus.InProgress);
        result.Value.CurrentSessionId.Should().Be(sessionId);
    }

    [Fact(Timeout = 5000)]
    public async SystemTask GetNextAvailableTaskAsync_DifferentSessionsClaimDifferentTasks()
    {
        // Arrange
        var session1 = ExecutionSession.Create(Guid.NewGuid(), "worker-001").Value;
        var session2 = ExecutionSession.Create(Guid.NewGuid(), "worker-002").Value;
        await _sessionRepository.AddAsync(session1, CancellationToken.None);
        await _sessionRepository.AddAsync(session2, CancellationToken.None);

        var task1 = IntegrationTestFactory.CreateTask(prompt: "Task 1", completionPromise: "DONE", maxIterations: 10);
        var task2 = IntegrationTestFactory.CreateTask(prompt: "Task 2", completionPromise: "DONE", maxIterations: 10);

        await _taskRepository.AddAsync(task1, CancellationToken.None);
        await _taskRepository.AddAsync(task2, CancellationToken.None);

        // Act
        var result1 = await _service.GetNextAvailableTaskAsync(session1.Id, CancellationToken.None);
        var result2 = await _service.GetNextAvailableTaskAsync(session2.Id, CancellationToken.None);

        // Assert
        result1.IsSuccess.Should().BeTrue();
        result2.IsSuccess.Should().BeTrue();
        result1.Value!.Id.Should().Be(task1.Id);
        result2.Value!.Id.Should().Be(task2.Id);
    }

    [Fact(Timeout = 5000)]
    public async SystemTask GetNextAvailableTaskAsync_WithNoAvailableTasks_ShouldReturnNull()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var session = ExecutionSession.Create(sessionId, "worker-001").Value;
        await _sessionRepository.AddAsync(session, CancellationToken.None);

        // Act
        var result = await _service.GetNextAvailableTaskAsync(sessionId, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact(Timeout = 5000)]
    public async SystemTask GetNextAvailableTaskAsync_SkipsInProgressTasks()
    {
        // Arrange
        var session1 = ExecutionSession.Create(Guid.NewGuid(), "worker-001").Value;
        var session2 = ExecutionSession.Create(Guid.NewGuid(), "worker-002").Value;
        await _sessionRepository.AddAsync(session1, CancellationToken.None);
        await _sessionRepository.AddAsync(session2, CancellationToken.None);

        var task1 = IntegrationTestFactory.CreateTask(prompt: "Task 1", completionPromise: "DONE", maxIterations: 10);
        var task2 = IntegrationTestFactory.CreateTask(prompt: "Task 2", completionPromise: "DONE", maxIterations: 10);

        await _taskRepository.AddAsync(task1, CancellationToken.None);
        await _taskRepository.AddAsync(task2, CancellationToken.None);

        // Claim task1
        var claimedTask = await _service.GetNextAvailableTaskAsync(session1.Id, CancellationToken.None);
        claimedTask.IsSuccess.Should().BeTrue();

        // Act - Try to claim next task as session2
        var result = await _service.GetNextAvailableTaskAsync(session2.Id, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value!.Id.Should().Be(task2.Id);
    }

    #endregion

    #region ReclaimStaleTasksAsync Integration Tests

    [Fact(Timeout = 5000)]
    public async SystemTask ReclaimStaleTasksAsync_WithStaleInProgressTasks_ShouldMarkAsAbandoned()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var session = ExecutionSession.Create(sessionId, "worker-001").Value;
        await _sessionRepository.AddAsync(session, CancellationToken.None);

        var task = IntegrationTestFactory.CreateTask(prompt: "Test task", completionPromise: "DONE", maxIterations: 10);
        await _taskRepository.AddAsync(task, CancellationToken.None);

        // Claim the task
        var claimResult = await _service.GetNextAvailableTaskAsync(sessionId, CancellationToken.None);
        claimResult.IsSuccess.Should().BeTrue();

        // Mark session as stale
        session.Heartbeat(DateTime.UtcNow.AddMinutes(-10));
        await _sessionRepository.UpdateAsync(session, CancellationToken.None);

        // Clear change tracker to ensure fresh query
        _dbContext.ChangeTracker.Clear();

        // Act
        var result = await _service.ReclaimStaleTasksAsync(CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();

        var reclaimedTask = await _taskRepository.GetByIdAsync(task.Id, CancellationToken.None);
        reclaimedTask.IsSuccess.Should().BeTrue();
        reclaimedTask.Value.Status.Should().Be(TaskStatus.Abandoned);
        reclaimedTask.Value.CurrentSessionId.Should().BeNull();
    }

    [Fact(Timeout = 5000)]
    public async SystemTask ReclaimStaleTasksAsync_WithRecentHeartbeat_ShouldNotReclaim()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var session = ExecutionSession.Create(sessionId, "worker-001").Value;
        await _sessionRepository.AddAsync(session, CancellationToken.None);

        var task = IntegrationTestFactory.CreateTask(prompt: "Test task", completionPromise: "DONE", maxIterations: 10);
        await _taskRepository.AddAsync(task, CancellationToken.None);

        // Claim the task
        await _service.GetNextAvailableTaskAsync(sessionId, CancellationToken.None);

        // Act
        var result = await _service.ReclaimStaleTasksAsync(CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();

        var taskAfterReclaim = await _taskRepository.GetByIdAsync(task.Id, CancellationToken.None);
        taskAfterReclaim.IsSuccess.Should().BeTrue();
        taskAfterReclaim.Value.Status.Should().Be(TaskStatus.InProgress);
    }

    [Fact(Timeout = 5000)]
    public async SystemTask ReclaimStaleTasksAsync_WithMultipleStaleTasks_ShouldReclaimAll()
    {
        // Arrange
        var session = ExecutionSession.Create(Guid.NewGuid(), "worker-001").Value;
        await _sessionRepository.AddAsync(session, CancellationToken.None);

        var task1 = IntegrationTestFactory.CreateTask(prompt: "Task 1", completionPromise: "DONE", maxIterations: 10);
        var task2 = IntegrationTestFactory.CreateTask(prompt: "Task 2", completionPromise: "DONE", maxIterations: 10);
        var task3 = IntegrationTestFactory.CreateTask(prompt: "Task 3", completionPromise: "DONE", maxIterations: 10);

        await _taskRepository.AddAsync(task1, CancellationToken.None);
        await _taskRepository.AddAsync(task2, CancellationToken.None);
        await _taskRepository.AddAsync(task3, CancellationToken.None);

        // Claim all tasks
        await _service.GetNextAvailableTaskAsync(session.Id, CancellationToken.None);
        var session2 = ExecutionSession.Create(Guid.NewGuid(), "worker-002").Value;
        await _sessionRepository.AddAsync(session2, CancellationToken.None);
        await _service.GetNextAvailableTaskAsync(session2.Id, CancellationToken.None);
        var session3 = ExecutionSession.Create(Guid.NewGuid(), "worker-003").Value;
        await _sessionRepository.AddAsync(session3, CancellationToken.None);
        await _service.GetNextAvailableTaskAsync(session3.Id, CancellationToken.None);

        // Mark sessions as stale
        session.Heartbeat(DateTime.UtcNow.AddMinutes(-10));
        session2.Heartbeat(DateTime.UtcNow.AddMinutes(-10));
        session3.Heartbeat(DateTime.UtcNow.AddMinutes(-10));
        await _sessionRepository.UpdateAsync(session, CancellationToken.None);
        await _sessionRepository.UpdateAsync(session2, CancellationToken.None);
        await _sessionRepository.UpdateAsync(session3, CancellationToken.None);

        // Clear change tracker to ensure fresh query
        _dbContext.ChangeTracker.Clear();

        // Act
        var result = await _service.ReclaimStaleTasksAsync(CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();

        var reclaimed1 = await _taskRepository.GetByIdAsync(task1.Id, CancellationToken.None);
        var reclaimed2 = await _taskRepository.GetByIdAsync(task2.Id, CancellationToken.None);
        var reclaimed3 = await _taskRepository.GetByIdAsync(task3.Id, CancellationToken.None);

        reclaimed1.Value.Status.Should().Be(TaskStatus.Abandoned);
        reclaimed2.Value.Status.Should().Be(TaskStatus.Abandoned);
        reclaimed3.Value.Status.Should().Be(TaskStatus.Abandoned);
    }

    [Fact(Timeout = 5000)]
    public async SystemTask ReclaimStaleTasksAsync_WithNoStaleTasks_ShouldSucceed()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var session = ExecutionSession.Create(sessionId, "worker-001").Value;
        await _sessionRepository.AddAsync(session, CancellationToken.None);

        var task = IntegrationTestFactory.CreateTask(prompt: "Test task", completionPromise: "DONE", maxIterations: 10);
        await _taskRepository.AddAsync(task, CancellationToken.None);
        await _service.GetNextAvailableTaskAsync(sessionId, CancellationToken.None);

        // Act
        var result = await _service.ReclaimStaleTasksAsync(CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var taskAfterReclaim = await _taskRepository.GetByIdAsync(task.Id, CancellationToken.None);
        taskAfterReclaim.Value.Status.Should().Be(TaskStatus.InProgress);
    }

    #endregion
}
