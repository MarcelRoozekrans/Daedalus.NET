using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SystemTask = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Repositories;

/// <summary>
///     Integration tests for ExecutionSessionRepository.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class ExecutionSessionRepositoryTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly ILogger<ExecutionSessionRepository>
        _logger = Substitute.For<ILogger<ExecutionSessionRepository>>();

    private ApplicationDbContext _dbContext = null!;
    private ExecutionSessionRepository _repository = null!;

    public async SystemTask InitializeAsync()
    {
        var options = PostgresFixture.CreateDbContextOptions(fixture.ConnectionString);

        _dbContext = new ApplicationDbContext(options);
        _repository = new ExecutionSessionRepository(_dbContext, _logger);

        // Clean database at end of each test, not start (see DisposeAsync)
        await SystemTask.CompletedTask;
    }

    public async SystemTask DisposeAsync()
    {
        // Clean database after test completes (not before)
        try
        {
            await fixture.DatabaseResetter.ResetAsync();
        }
        catch
        {
            /* Ignore cleanup errors */
        }

        if (_dbContext != null)
        {
            await _dbContext.DisposeAsync();
        }
    }

    #region Concurrent Access Tests

    [Fact]
    public async SystemTask ConcurrentSessionCreations_ShouldNotConflict()
    {
        // Arrange
        var tasks = new List<SystemTask>();

        // Act - Each concurrent task needs its own DbContext (DbContext is not thread-safe)
        for (var i = 0; i < 10; i++)
        {
            var task = SystemTask.Run(async () =>
            {
                var options = PostgresFixture.CreateDbContextOptions(fixture.ConnectionString);
                using var taskDbContext = new ApplicationDbContext(options);
                var taskRepository = new ExecutionSessionRepository(taskDbContext, _logger);

                var session = ExecutionSession.Create(Guid.NewGuid(), $"worker-{Guid.NewGuid()}").Value;
                await taskRepository.AddAsync(session, CancellationToken.None);
            });
            tasks.Add(task);
        }

        await SystemTask.WhenAll(tasks);

        // Assert
        var result = await _repository.GetActiveAsync(CancellationToken.None);
        result.Value.Should().HaveCount(10);
    }

    #endregion

    #region GetByIdAsync Tests

    [Fact]
    public async SystemTask GetByIdAsync_WithExistingSession_ShouldReturnSession()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var session = ExecutionSession.Create(sessionId, "worker-001").Value;
        await _repository.AddAsync(session, CancellationToken.None);

        // Act
        var result = await _repository.GetByIdAsync(sessionId, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be(sessionId);
        result.Value.WorkerName.Should().Be("worker-001");
        result.Value.IsActive.Should().BeTrue();
    }

    [Fact]
    public async SystemTask GetByIdAsync_WithNonexistentId_ShouldReturnFailure()
    {
        // Arrange
        var nonexistentId = Guid.NewGuid();

        // Act
        var result = await _repository.GetByIdAsync(nonexistentId, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("not found");
    }

    [Fact]
    public async SystemTask GetByIdAsync_ShouldReturnCorrectSession()
    {
        // Arrange
        var session1 = ExecutionSession.Create(Guid.NewGuid(), "worker-001").Value;
        var session2 = ExecutionSession.Create(Guid.NewGuid(), "worker-002").Value;

        await _repository.AddAsync(session1, CancellationToken.None);
        await _repository.AddAsync(session2, CancellationToken.None);

        // Act
        var result = await _repository.GetByIdAsync(session1.Id, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be(session1.Id);
        result.Value.WorkerName.Should().Be("worker-001");
    }

    #endregion

    #region AddAsync Tests

    [Fact]
    public async SystemTask AddAsync_WithValidSession_ShouldSucceed()
    {
        var sessionId = Guid.NewGuid();
        var session = ExecutionSession.Create(sessionId, "worker-001").Value;

        var result = await _repository.AddAsync(session, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be(sessionId);

        var retrieved = await _repository.GetByIdAsync(sessionId, CancellationToken.None);
        retrieved.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async SystemTask AddAsync_MultipleSessionsWithDifferentIds_ShouldSucceed()
    {
        var session1 = ExecutionSession.Create(Guid.NewGuid(), "worker-001").Value;
        var session2 = ExecutionSession.Create(Guid.NewGuid(), "worker-002").Value;

        var result1 = await _repository.AddAsync(session1, CancellationToken.None);
        var result2 = await _repository.AddAsync(session2, CancellationToken.None);

        result1.IsSuccess.Should().BeTrue();
        result2.IsSuccess.Should().BeTrue();
        result1.Value.Id.Should().NotBe(result2.Value.Id);
    }

    [Fact]
    public async SystemTask AddAsync_ShouldPreserveSessionProperties()
    {
        var sessionId = Guid.NewGuid();
        var session = ExecutionSession.Create(sessionId, "worker-GPU-001").Value;
        for (var i = 0; i < 5; i++)
            session.RecordTaskCompleted();

        await _repository.AddAsync(session, CancellationToken.None);

        var retrieved = await _repository.GetByIdAsync(sessionId, CancellationToken.None);
        retrieved.Value.TasksCompleted.Should().Be(5);
        retrieved.Value.WorkerName.Should().Be("worker-GPU-001");
    }

    #endregion

    #region UpdateAsync Tests

    [Fact]
    public async SystemTask UpdateAsync_UpdatingHeartbeat_ShouldPersist()
    {
        var sessionId = Guid.NewGuid();
        var session = ExecutionSession.Create(sessionId, "worker-001").Value;
        await _repository.AddAsync(session, CancellationToken.None);

        var oldHeartbeat = session.LastHeartbeat;
        await SystemTask.Delay(100);

        session.Heartbeat();

        var updateResult = await _repository.UpdateAsync(session, CancellationToken.None);

        updateResult.IsSuccess.Should().BeTrue();

        var retrieved = await _repository.GetByIdAsync(sessionId, CancellationToken.None);
        retrieved.Value.LastHeartbeat.Should().BeAfter(oldHeartbeat);
    }

    [Fact]
    public async SystemTask UpdateAsync_ShuttingDownSession_ShouldPersist()
    {
        var sessionId = Guid.NewGuid();
        var session = ExecutionSession.Create(sessionId, "worker-001").Value;
        await _repository.AddAsync(session, CancellationToken.None);

        session.Shutdown();

        var updateResult = await _repository.UpdateAsync(session, CancellationToken.None);

        updateResult.IsSuccess.Should().BeTrue();

        var retrieved = await _repository.GetByIdAsync(sessionId, CancellationToken.None);
        retrieved.Value.IsActive.Should().BeFalse();
    }

    [Fact]
    public async SystemTask UpdateAsync_UpdatingTasksCompleted_ShouldPersist()
    {
        var sessionId = Guid.NewGuid();
        var session = ExecutionSession.Create(sessionId, "worker-001").Value;
        await _repository.AddAsync(session, CancellationToken.None);

        for (var i = 0; i < 10; i++)
            session.RecordTaskCompleted();

        var updateResult = await _repository.UpdateAsync(session, CancellationToken.None);

        updateResult.IsSuccess.Should().BeTrue();

        var retrieved = await _repository.GetByIdAsync(sessionId, CancellationToken.None);
        retrieved.Value.TasksCompleted.Should().Be(10);
    }

    [Fact]
    public async SystemTask UpdateAsync_MultipleUpdates_ShouldReflectLatestChanges()
    {
        var sessionId = Guid.NewGuid();
        var session = ExecutionSession.Create(sessionId, "worker-001").Value;
        await _repository.AddAsync(session, CancellationToken.None);

        for (var i = 0; i < 5; i++)
            session.RecordTaskCompleted();
        await _repository.UpdateAsync(session, CancellationToken.None);
        var retrieved1 = await _repository.GetByIdAsync(sessionId, CancellationToken.None);
        retrieved1.Value.TasksCompleted.Should().Be(5);

        for (var i = 0; i < 5; i++)
            session.RecordTaskCompleted();
        await _repository.UpdateAsync(session, CancellationToken.None);
        var retrieved2 = await _repository.GetByIdAsync(sessionId, CancellationToken.None);
        retrieved2.Value.TasksCompleted.Should().Be(10);

        session.Heartbeat();
        await _repository.UpdateAsync(session, CancellationToken.None);
        var retrieved3 = await _repository.GetByIdAsync(sessionId, CancellationToken.None);
        retrieved3.Value.LastHeartbeat.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
    }

    #endregion

    #region GetActiveAsync Tests

    [Fact]
    public async SystemTask GetActiveAsync_WithActiveSessions_ShouldReturnSessions()
    {
        var session1 = ExecutionSession.Create(Guid.NewGuid(), "worker-001").Value;
        var session2 = ExecutionSession.Create(Guid.NewGuid(), "worker-002").Value;

        await _repository.AddAsync(session1, CancellationToken.None);
        await _repository.AddAsync(session2, CancellationToken.None);

        var result = await _repository.GetActiveAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
    }

    [Fact]
    public async SystemTask GetActiveAsync_WithInactiveSessions_ShouldNotReturnInactiveSessions()
    {
        var activeSession = ExecutionSession.Create(Guid.NewGuid(), "worker-001").Value;
        var inactiveSession = ExecutionSession.Create(Guid.NewGuid(), "worker-002").Value;

        await _repository.AddAsync(activeSession, CancellationToken.None);
        await _repository.AddAsync(inactiveSession, CancellationToken.None);

        inactiveSession.Shutdown();
        await _repository.UpdateAsync(inactiveSession, CancellationToken.None);

        var result = await _repository.GetActiveAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
        result.Value[0].Id.Should().Be(activeSession.Id);
    }

    [Fact]
    public async SystemTask GetActiveAsync_WithNoSessions_ShouldReturnEmptyList()
    {
        var result = await _repository.GetActiveAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async SystemTask GetActiveAsync_ShouldReturnOrderedByStartedAt()
    {
        var session1 = ExecutionSession.Create(Guid.NewGuid(), "worker-001").Value;
        await _repository.AddAsync(session1, CancellationToken.None);

        await SystemTask.Delay(100);

        var session2 = ExecutionSession.Create(Guid.NewGuid(), "worker-002").Value;
        await _repository.AddAsync(session2, CancellationToken.None);

        var result = await _repository.GetActiveAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
        result.Value[0].Id.Should().Be(session1.Id);
        result.Value[1].Id.Should().Be(session2.Id);
    }

    [Fact]
    public async SystemTask GetActiveAsync_WithMultipleActiveSessions_ShouldReturnAll()
    {
        var sessions = new List<ExecutionSession>();
        for (var i = 0; i < 5; i++)
        {
            var session = ExecutionSession.Create(Guid.NewGuid(), $"worker-{i:D3}").Value;
            await _repository.AddAsync(session, CancellationToken.None);
            sessions.Add(session);
        }

        var result = await _repository.GetActiveAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(5);
    }

    #endregion

    #region Session Lifecycle Tests

    [Fact]
    public async SystemTask SessionLifecycle_CreateShutdownRetrieve()
    {
        var sessionId = Guid.NewGuid();
        var session = ExecutionSession.Create(sessionId, "worker-001").Value;
        await _repository.AddAsync(session, CancellationToken.None);

        var active1 = await _repository.GetActiveAsync(CancellationToken.None);
        active1.Value.Should().HaveCount(1);

        session.Shutdown();
        await _repository.UpdateAsync(session, CancellationToken.None);

        var active2 = await _repository.GetActiveAsync(CancellationToken.None);

        active2.Value.Should().BeEmpty();
    }

    [Fact]
    public async SystemTask SessionLifecycle_MultipleHeartbeatsWithUpdates()
    {
        var sessionId = Guid.NewGuid();
        var session = ExecutionSession.Create(sessionId, "worker-001").Value;
        await _repository.AddAsync(session, CancellationToken.None);

        var initialHeartbeat = session.LastHeartbeat;

        for (var i = 0; i < 3; i++)
        {
            await SystemTask.Delay(50);
            session.Heartbeat();
            await _repository.UpdateAsync(session, CancellationToken.None);
        }

        var retrieved = await _repository.GetByIdAsync(sessionId, CancellationToken.None);
        retrieved.Value.LastHeartbeat.Should().BeAfter(initialHeartbeat);
    }

    #endregion
}
