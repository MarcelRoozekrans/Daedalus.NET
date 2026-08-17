using System.Collections.Concurrent;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using DomainTaskStatus = Daedalus.Domain.Entities.TaskStatus;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Services;

/// <summary>
///     Concurrency tests for multi-worker task execution and session management.
///     Tests transaction isolation and consistency with real PostgreSQL database.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class TaskExecutionConcurrencyTests(PostgresFixture fixture) : IAsyncLifetime
{
    private string _connectionString = null!;
    private ApplicationDbContext _dbContext = null!;

    public async Task InitializeAsync()
    {
        await fixture.DatabaseResetter.ResetAsync();

        _connectionString = fixture.ConnectionString;
        var options = PostgresFixture.CreateDbContextOptions(_connectionString);

        _dbContext = new ApplicationDbContext(options);

        // Create default project for tests
        var project = IntegrationTestFactory.CreateProject(IntegrationTestFactory.GetDefaultProjectId());
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        if (_dbContext != null)
        {
            await _dbContext.DisposeAsync();
        }
    }

    [Fact(Timeout = 10000)]
    public async Task SingleTask_RecordingMultipleExecutions()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        var taskEntity = IntegrationTestFactory.CreateTask(taskId);
        _dbContext.Tasks.Add(taskEntity);

        var session = ExecutionSession.Create(sessionId, "test-worker").Value;
        _dbContext.ExecutionSessions.Add(session);
        await _dbContext.SaveChangesAsync();

        // Claim task
        taskEntity.Claim(sessionId);
        await _dbContext.SaveChangesAsync();

        // Act - Record first execution
        var exec1 = new TaskExecution
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
        _dbContext.TaskExecutions.Add(exec1);
        taskEntity.RecordExecution(exec1);
        await _dbContext.SaveChangesAsync();

        // Record second execution
        var exec2 = new TaskExecution
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            SessionId = sessionId,
            IterationNumber = 2,
            Prompt = "prompt-2",
            LlmResponse = "completion found",
            CompletionPromiseFound = true,
            ExecutionDuration = TimeSpan.FromSeconds(1)
        };
        _dbContext.TaskExecutions.Add(exec2);
        taskEntity.RecordExecution(exec2);
        await _dbContext.SaveChangesAsync();

        // Assert - Clear tracker and reload from database to avoid duplicates from in-memory changes
        _dbContext.ChangeTracker.Clear();
        var refreshedTask = await _dbContext.Tasks.Include(t => t.Executions)
            .FirstAsync(t => t.Id == taskId);
        refreshedTask.Executions.Should().HaveCount(2);
        refreshedTask.Status.Should().Be(DomainTaskStatus.Completed);
        refreshedTask.IterationCount.Should().Be(2);
    }

    [Fact(Timeout = 10000)]
    public async Task RapidSessionCreation_AllSessionsUnique()
    {
        // Arrange
        var createdIds = new ConcurrentBag<Guid>();

        // Act - Create sessions rapidly in parallel
        var createTasks = Enumerable.Range(0, 5).Select(i => Task.Run(async () =>
        {
            // Each thread needs its own DbContext instance
            var options = PostgresFixture.CreateDbContextOptions(_connectionString);

            using var dbContext = new ApplicationDbContext(options);

            var sessionId = Guid.NewGuid();
            var session = ExecutionSession.Create(sessionId, $"rapid-worker-{i}").Value;
            dbContext.ExecutionSessions.Add(session);
            await dbContext.SaveChangesAsync();

            createdIds.Add(sessionId);
        }));

        await Task.WhenAll(createTasks);

        // Assert
        createdIds.Should().HaveCount(5);
        createdIds.Distinct().Should().HaveCount(5);
    }

    [Fact(Timeout = 10000)]
    public async Task SessionHeartbeat_ConcurrentUpdates()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var session = ExecutionSession.Create(sessionId, "heartbeat-worker").Value;
        _dbContext.ExecutionSessions.Add(session);
        await _dbContext.SaveChangesAsync();

        var originalHeartbeat = session.LastHeartbeat;

        // Act - Multiple concurrent heartbeat updates
        var heartbeatTasks = Enumerable.Range(0, 3).Select(i => Task.Run(async () =>
        {
            await Task.Delay(10 * i);
            session.Heartbeat();
            await _dbContext.SaveChangesAsync();
        }));

        await Task.WhenAll(heartbeatTasks);

        // Assert
        session.LastHeartbeat.Should().BeAfter(originalHeartbeat);
    }

    [Fact(Timeout = 10000)]
    public async Task TaskExecution_SequentialIterations()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        var taskEntity = IntegrationTestFactory.CreateTask(taskId, prompt: "Sequential Task",
            completionPromise: "promise", maxIterations: 5);
        _dbContext.Tasks.Add(taskEntity);

        var session = ExecutionSession.Create(sessionId, "seq-worker").Value;
        _dbContext.ExecutionSessions.Add(session);
        await _dbContext.SaveChangesAsync();

        taskEntity.Claim(sessionId);
        await _dbContext.SaveChangesAsync();

        // Act - Record executions sequentially
        for (var i = 1; i <= 3; i++)
        {
            var execution = new TaskExecution
            {
                Id = Guid.NewGuid(),
                TaskId = taskId,
                SessionId = sessionId,
                IterationNumber = i,
                Prompt = $"prompt-{i}",
                LlmResponse = $"response-{i}",
                CompletionPromiseFound = false,
                ExecutionDuration = TimeSpan.FromSeconds(1)
            };
            _dbContext.TaskExecutions.Add(execution);
            taskEntity.RecordExecution(execution);
            await _dbContext.SaveChangesAsync();
        }

        // Assert - Clear tracker and reload task from database to get fresh state
        _dbContext.ChangeTracker.Clear();
        var refreshedTask = await _dbContext.Tasks.Include(t => t.Executions)
            .FirstAsync(t => t.Id == taskId);
        refreshedTask.Executions.Should().HaveCount(3);
        refreshedTask.Executions.Select(e => e.IterationNumber).Order().Should().Equal(1, 2, 3);
    }

    [Fact(Timeout = 10000)]
    public async Task SessionShutdown_AllowsRestart()
    {
        // Arrange
        var sessionId1 = Guid.NewGuid();
        var session1 = ExecutionSession.Create(sessionId1, "shutdown-worker").Value;
        _dbContext.ExecutionSessions.Add(session1);
        await _dbContext.SaveChangesAsync();

        // Act - Shutdown first session
        session1.Shutdown();
        await _dbContext.SaveChangesAsync();

        // Create new session after shutdown
        var sessionId2 = Guid.NewGuid();
        var session2 = ExecutionSession.Create(sessionId2, "shutdown-worker").Value;
        _dbContext.ExecutionSessions.Add(session2);
        await _dbContext.SaveChangesAsync();

        // Assert
        session1.IsActive.Should().BeFalse();
        session2.IsActive.Should().BeTrue();

        var allSessions = await _dbContext.ExecutionSessions.ToListAsync();
        allSessions.Should().HaveCount(2);
    }
}
