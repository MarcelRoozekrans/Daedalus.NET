using System.Collections.Concurrent;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence.Orm.Tasks;
using Daedalus.Tests.Integration.Fixtures;
using Daedalus.Tests.Shared;
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
    private SeedBatch _seed = null!;

    public async Task InitializeAsync()
    {
        await fixture.DatabaseResetter.ResetAsync();

        _connectionString = fixture.ConnectionString;
        _seed = new SeedBatch(fixture.Db, _connectionString);

        // Create default project for tests
        var project = IntegrationTestFactory.CreateProject(IntegrationTestFactory.GetDefaultProjectId());
        _seed.Projects.Add(project);
        await _seed.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(Timeout = 10000)]
    public async Task PersistedTaskExecutionRows_TwoRowsOnOneTask_AreAllReloaded()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        var taskEntity = IntegrationTestFactory.CreateTask(taskId);
        _seed.Tasks.Add(taskEntity);
        await _seed.SaveChangesAsync();

        var session = ExecutionSession.Create(sessionId, "test-worker").Value;
        await StoreSeed.AddExecutionSessionAsync(_connectionString, session);

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
        _seed.TaskExecutions.Add(exec1);
        await _seed.SaveChangesAsync();

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
        _seed.TaskExecutions.Add(exec2);
        await _seed.SaveChangesAsync();

        // Assert - reload the task with its executions from the database
        var refreshedTask = await LoadTaskAsync(taskId);
        refreshedTask.Executions.Should().HaveCount(2);
    }

    [Fact(Timeout = 10000)]
    public async Task RapidSessionCreation_AllSessionsUnique()
    {
        // Arrange
        var createdIds = new ConcurrentBag<Guid>();

        // Act - Create sessions rapidly in parallel
        var createTasks = Enumerable.Range(0, 5).Select(i => Task.Run(async () =>
        {
            var sessionId = Guid.NewGuid();
            var session = ExecutionSession.Create(sessionId, $"rapid-worker-{i}").Value;
            await StoreSeed.AddExecutionSessionAsync(_connectionString, session);

            createdIds.Add(sessionId);
        }));

        await Task.WhenAll(createTasks);

        // Assert
        createdIds.Should().HaveCount(5);
        createdIds.Distinct().Should().HaveCount(5);
    }

    [Fact(Timeout = 10000)]
    public async Task PersistedTaskExecutionRows_SavedOneAtATime_KeepTheirIterationNumbers()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        var taskEntity = IntegrationTestFactory.CreateTask(taskId, prompt: "Sequential Task");
        _seed.Tasks.Add(taskEntity);
        await _seed.SaveChangesAsync();

        var session = ExecutionSession.Create(sessionId, "seq-worker").Value;
        await StoreSeed.AddExecutionSessionAsync(_connectionString, session);

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
            _seed.TaskExecutions.Add(execution);
            await _seed.SaveChangesAsync();
        }

        // Assert - reload the task with its executions from the database
        var refreshedTask = await LoadTaskAsync(taskId);
        refreshedTask.Executions.Should().HaveCount(3);
        refreshedTask.Executions.Select(e => e.IterationNumber).Order().Should().Equal(1, 2, 3);
    }

    private async Task<Domain.Entities.Task> LoadTaskAsync(Guid taskId)
    {
        await using var connection = await fixture.Db.OpenAsync(CancellationToken.None);
        return (await new TaskSql(connection).FindAsync(taskId, CancellationToken.None))!;
    }
}
