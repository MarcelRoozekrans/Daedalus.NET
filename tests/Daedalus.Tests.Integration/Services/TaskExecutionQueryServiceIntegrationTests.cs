using Daedalus.Api.Services;
using Daedalus.Domain.Entities;
using Daedalus.Tests.Integration.Fixtures;
using Daedalus.Tests.Shared;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Services;

/// <summary>
///     Integration tests for TaskExecutionQueryService.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class TaskExecutionQueryServiceIntegrationTests(PostgresFixture fixture) : IAsyncLifetime
{
    private SeedBatch _seed = null!;
    private TaskExecutionQueryService _sut = null!;

    public async Task InitializeAsync()
    {
        await fixture.DatabaseResetter.ResetAsync();

        _seed = new SeedBatch(fixture.Db, fixture.ConnectionString);

        // Create default project for tests
        var project = IntegrationTestFactory.CreateProject(IntegrationTestFactory.GetDefaultProjectId());
        _seed.Projects.Add(project);
        await _seed.SaveChangesAsync();

        _sut = new TaskExecutionQueryService(fixture.Db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(Timeout = 5000)]
    public async Task GetByTaskIdAsync_ReturnsExecutionsForTask()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var task = IntegrationTestFactory.CreateTask(taskId, prompt: "Test");

        var execution = new TaskExecution
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            SessionId = sessionId,
            IterationNumber = 1,
            Prompt = "Prompt",
            LlmResponse = "Response",
            CompletionPromiseFound = false,
            ExecutionDuration = TimeSpan.FromSeconds(1)
        };
        _seed.Tasks.Add(task);
        _seed.TaskExecutions.Add(execution);
        await _seed.SaveChangesAsync();

        // Act
        var result = await _sut.GetByTaskIdAsync(taskId);

        // Assert
        result.Items.Should().HaveCount(1);
        result.Items[0].TaskId.Should().Be(taskId);
    }

    [Fact(Timeout = 5000)]
    public async Task GetBySessionIdAsync_ReturnsExecutionsForSession()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var task = IntegrationTestFactory.CreateTask(taskId, prompt: "Test");

        var execution = new TaskExecution
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            SessionId = sessionId,
            IterationNumber = 1,
            Prompt = "Prompt",
            LlmResponse = "Response",
            CompletionPromiseFound = false,
            ExecutionDuration = TimeSpan.FromSeconds(1)
        };
        _seed.Tasks.Add(task);
        _seed.TaskExecutions.Add(execution);
        await _seed.SaveChangesAsync();

        // Act
        var result = await _sut.GetBySessionIdAsync(sessionId);

        // Assert
        result.Items.Should().HaveCount(1);
        result.Items[0].SessionId.Should().Be(sessionId);
    }
}
