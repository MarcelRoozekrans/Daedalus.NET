using Daedalus.Api.Services;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using SystemTask = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Services;

/// <summary>
///     Integration tests for API Query Services.
///     Tests query operations with real PostgreSQL database context.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class TaskQueryServiceIntegrationTests(PostgresFixture fixture) : IAsyncLifetime
{
    private ApplicationDbContext _dbContext = null!;
    private TaskQueryService _sut = null!;

    public async SystemTask InitializeAsync()
    {
        await fixture.DatabaseResetter.ResetAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;

        _dbContext = new ApplicationDbContext(options);

        // Create default project for tests
        var project = IntegrationTestFactory.CreateProject(IntegrationTestFactory.GetDefaultProjectId());
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync();

        _sut = new TaskQueryService(_dbContext);
    }

    public async SystemTask DisposeAsync()
    {
        if (_dbContext != null)
        {
            await _dbContext.DisposeAsync();
        }
    }

    [Fact(Timeout = 5000)]
    public async SystemTask GetAllAsync_WithTasks_ReturnsPaginatedResults()
    {
        // Arrange
        var taskId1 = Guid.NewGuid();
        var taskId2 = Guid.NewGuid();
        var task1 = IntegrationTestFactory.CreateTask(taskId1, prompt: "Task 1", completionPromise: "Promise 1",
            maxIterations: 5);
        var task2 = IntegrationTestFactory.CreateTask(taskId2, prompt: "Task 2", completionPromise: "Promise 2",
            maxIterations: 5);

        _dbContext.Tasks.AddRange(task1, task2);
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _sut.GetAllAsync();

        // Assert
        result.Items.Should().HaveCount(2);
        result.Total.Should().Be(2);
    }

    [Fact(Timeout = 5000)]
    public async SystemTask GetByIdAsync_WithValidId_ReturnsTask()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var task = IntegrationTestFactory.CreateTask(taskId, prompt: "Test Task", completionPromise: "Test Promise",
            maxIterations: 5);

        _dbContext.Tasks.Add(task);
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _sut.GetByIdAsync(taskId);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(taskId);
        result.Prompt.Should().Be("Test Task");
    }

    [Fact(Timeout = 5000)]
    public async SystemTask GetByIdAsync_WithInvalidId_ReturnsNull()
    {
        // Act
        var result = await _sut.GetByIdAsync(Guid.NewGuid());

        // Assert
        result.Should().BeNull();
    }
}
