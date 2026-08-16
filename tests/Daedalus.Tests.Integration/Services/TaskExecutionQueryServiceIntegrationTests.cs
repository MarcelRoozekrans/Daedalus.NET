using Daedalus.Api.Services;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Services;

/// <summary>
///     Integration tests for TaskExecutionQueryService.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class TaskExecutionQueryServiceIntegrationTests(PostgresFixture fixture) : IAsyncLifetime
{
    private ApplicationDbContext _dbContext = null!;
    private TaskExecutionQueryService _sut = null!;

    public async Task InitializeAsync()
    {
        await fixture.DatabaseResetter.ResetAsync();

        var options = PostgresFixture.CreateDbContextOptions(fixture.ConnectionString);

        _dbContext = new ApplicationDbContext(options);

        // Create default project for tests
        var project = IntegrationTestFactory.CreateProject(IntegrationTestFactory.GetDefaultProjectId());
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync();

        _sut = new TaskExecutionQueryService(_dbContext);
    }

    public async Task DisposeAsync()
    {
        if (_dbContext != null)
        {
            await _dbContext.DisposeAsync();
        }
    }

    [Fact(Timeout = 5000)]
    public async Task GetByTaskIdAsync_ReturnsExecutionsForTask()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var task = IntegrationTestFactory.CreateTask(taskId, prompt: "Test", completionPromise: "Promise",
            maxIterations: 5);

        // Claim task before recording execution
        task.Claim(sessionId);

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
        task.RecordExecution(execution);

        _dbContext.Tasks.Add(task);
        await _dbContext.SaveChangesAsync();

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
        var task = IntegrationTestFactory.CreateTask(taskId, prompt: "Test", completionPromise: "Promise",
            maxIterations: 5);

        // Claim task before recording execution
        task.Claim(sessionId);

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
        task.RecordExecution(execution);

        _dbContext.Tasks.Add(task);
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _sut.GetBySessionIdAsync(sessionId);

        // Assert
        result.Items.Should().HaveCount(1);
        result.Items[0].SessionId.Should().Be(sessionId);
    }
}
