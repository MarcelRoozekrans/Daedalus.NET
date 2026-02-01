using Daedalus.Application.Services;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.Logging;
using DomainTaskStatus = Daedalus.Domain.Entities.TaskStatus;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Services;

/// <summary>
///     Integration tests for TaskAssignmentService using Aspire-managed PostgreSQL.
///     Demonstrates modern integration test patterns with Aspire orchestration.
/// </summary>
[Collection(AspireDatabaseCollection.Name)]
public class TaskAssignmentServiceAspireIntegrationTests(AspirePostgresFixture fixture)
    : AspireIntegrationTestBase(fixture)
{
    private readonly ILogger<TaskAssignmentService> _mockLogger = Substitute.For<ILogger<TaskAssignmentService>>();

    private readonly ILogger<ExecutionSessionRepository> _sessionLoggerMock =
        Substitute.For<ILogger<ExecutionSessionRepository>>();

    private readonly ILogger<TaskRepository> _taskLoggerMock = Substitute.For<ILogger<TaskRepository>>();
    private TaskAssignmentService _service = null!;
    private ExecutionSessionRepository _sessionRepository = null!;
    private TaskRepository _taskRepository = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        _taskRepository = new TaskRepository(_dbContext, _taskLoggerMock);
        _sessionRepository = new ExecutionSessionRepository(_dbContext, _sessionLoggerMock);
        _service = new TaskAssignmentService(_taskRepository, _mockLogger);
    }

    #region CompleteTaskAsync Integration Tests

    [Fact(Timeout = 5000)]
    public async Task GetNextAvailableTaskAsync_WithPendingTask_ShouldReturnTask()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var session = ExecutionSession.Create(sessionId, "worker-001").Value;
        await _sessionRepository.AddAsync(session, CancellationToken.None);

        var task = IntegrationTestFactory.CreateTask(prompt: "Task to complete", completionPromise: "DONE",
            maxIterations: 10);
        await _taskRepository.AddAsync(task, CancellationToken.None);

        // Act
        var result = await _service.GetNextAvailableTaskAsync(sessionId, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Status.Should().Be(DomainTaskStatus.InProgress);
    }

    #endregion

    #region GetNextAvailableTaskAsync Integration Tests

    [Fact(Timeout = 5000)]
    public async Task GetNextAvailableTaskAsync_WithMultiplePendingTasks_ShouldClaimFirstByCreatedDate()
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
        await Task.Delay(10);
        await _taskRepository.AddAsync(task2, CancellationToken.None);
        await Task.Delay(10);
        await _taskRepository.AddAsync(task3, CancellationToken.None);

        // Act
        var result = await _service.GetNextAvailableTaskAsync(sessionId, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Id.Should().Be(task1.Id);
        result.Value.Status.Should().Be(DomainTaskStatus.InProgress);
        result.Value.CurrentSessionId.Should().Be(sessionId);
    }

    [Fact(Timeout = 5000)]
    public async Task GetNextAvailableTaskAsync_WithNoAvailableTasks_ShouldReturnNull()
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
    public async Task GetNextAvailableTaskAsync_DifferentSessionsClaimDifferentTasks()
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

    #endregion
}
