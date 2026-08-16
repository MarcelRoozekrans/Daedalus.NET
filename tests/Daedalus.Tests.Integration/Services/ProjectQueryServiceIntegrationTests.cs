using Daedalus.Infrastructure.Persistence;
using Daedalus.Infrastructure.Services;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using SystemTask = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Services;

/// <summary>
///     Integration tests for ProjectQueryService.
///     Tests query operations with real PostgreSQL database context.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class ProjectQueryServiceIntegrationTests(PostgresFixture fixture) : IAsyncLifetime
{
    private ApplicationDbContext _dbContext = null!;
    private ProjectQueryService _sut = null!;

    public async SystemTask InitializeAsync()
    {
        await fixture.DatabaseResetter.ResetAsync();

        var options = PostgresFixture.CreateDbContextOptions(fixture.ConnectionString);

        _dbContext = new ApplicationDbContext(options);
        _sut = new ProjectQueryService(_dbContext);
    }

    public async SystemTask DisposeAsync()
    {
        if (_dbContext != null)
        {
            await _dbContext.DisposeAsync();
        }
    }

    #region GetAllAsync

    [Fact(Timeout = 5000)]
    public async SystemTask GetAllAsync_WithNoProjects_ReturnsEmptyResult()
    {
        // Act
        var result = await _sut.GetAllAsync(1, 10);

        // Assert
        result.Items.Should().BeEmpty();
        result.Total.Should().Be(0);
        result.Page.Should().Be(1);
        result.PageSize.Should().Be(10);
    }

    [Fact(Timeout = 5000)]
    public async SystemTask GetAllAsync_WithProjects_ReturnsPaginatedResults()
    {
        // Arrange
        _dbContext.Projects.Add(IntegrationTestFactory.CreateProject(name: "Project A", description: "First"));
        _dbContext.Projects.Add(IntegrationTestFactory.CreateProject(name: "Project B", description: "Second"));
        _dbContext.Projects.Add(IntegrationTestFactory.CreateProject(name: "Project C", description: "Third"));
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _sut.GetAllAsync(1, 10);

        // Assert
        result.Items.Should().HaveCount(3);
        result.Total.Should().Be(3);
    }

    [Fact(Timeout = 5000)]
    public async SystemTask GetAllAsync_WithPagination_ReturnsCorrectPage()
    {
        // Arrange
        for (var i = 0; i < 5; i++)
        {
            _dbContext.Projects.Add(IntegrationTestFactory.CreateProject(name: $"Project {i:D2}", description: $"Desc {i}"));
        }

        await _dbContext.SaveChangesAsync();

        // Act
        var page1 = await _sut.GetAllAsync(1, 2);
        var page2 = await _sut.GetAllAsync(2, 2);
        var page3 = await _sut.GetAllAsync(3, 2);

        // Assert
        page1.Items.Should().HaveCount(2);
        page2.Items.Should().HaveCount(2);
        page3.Items.Should().HaveCount(1);
        page1.Total.Should().Be(5);
    }

    [Fact(Timeout = 5000)]
    public async SystemTask GetAllAsync_ReturnsSortedByName()
    {
        // Arrange
        _dbContext.Projects.Add(IntegrationTestFactory.CreateProject(name: "Zebra", description: "Last"));
        _dbContext.Projects.Add(IntegrationTestFactory.CreateProject(name: "Alpha", description: "First"));
        _dbContext.Projects.Add(IntegrationTestFactory.CreateProject(name: "Middle", description: "Mid"));
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _sut.GetAllAsync(1, 10);

        // Assert
        result.Items[0].ProjectName.Should().Be("Alpha");
        result.Items[1].ProjectName.Should().Be("Middle");
        result.Items[2].ProjectName.Should().Be("Zebra");
    }

    [Fact(Timeout = 5000)]
    public async SystemTask GetAllAsync_IncludesTasksInResult()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var project = IntegrationTestFactory.CreateProject(projectId, "Project With Tasks", "Has tasks");
        _dbContext.Projects.Add(project);
        _dbContext.Tasks.Add(IntegrationTestFactory.CreateTask(projectId: projectId, prompt: "Task 1",
            completionPromise: "Promise", maxIterations: 5));
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _sut.GetAllAsync(1, 10);

        // Assert
        var projectDto = result.Items.Single(p => p.Id == projectId);
        projectDto.Tasks.Should().HaveCount(1);
    }

    [Fact(Timeout = 5000)]
    public async SystemTask GetAllAsync_MapsAllProjectFields()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        _dbContext.Projects.Add(IntegrationTestFactory.CreateProject(projectId, "Full Project", "Full description"));
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _sut.GetAllAsync(1, 10);

        // Assert
        var dto = result.Items[0];
        dto.Id.Should().Be(projectId);
        dto.ProjectName.Should().Be("Full Project");
        dto.Description.Should().Be("Full description");
        dto.Version.Should().NotBeNull();
        dto.CreatedAt.Should().NotBe(default);
    }

    #endregion

    #region GetByIdAsync

    [Fact(Timeout = 5000)]
    public async SystemTask GetByIdAsync_WithValidId_ReturnsProject()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        _dbContext.Projects.Add(IntegrationTestFactory.CreateProject(projectId, "Test Project", "Test description"));
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _sut.GetByIdAsync(projectId);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(projectId);
        result.ProjectName.Should().Be("Test Project");
        result.Description.Should().Be("Test description");
    }

    [Fact(Timeout = 5000)]
    public async SystemTask GetByIdAsync_WithInvalidId_ReturnsNull()
    {
        // Act
        var result = await _sut.GetByIdAsync(Guid.NewGuid());

        // Assert
        result.Should().BeNull();
    }

    [Fact(Timeout = 5000)]
    public async SystemTask GetByIdAsync_ReturnsEmptyTasksList()
    {
        // Arrange - GetByIdAsync does not include tasks
        var projectId = Guid.NewGuid();
        var project = IntegrationTestFactory.CreateProject(projectId, "Project", "Desc");
        _dbContext.Projects.Add(project);
        _dbContext.Tasks.Add(IntegrationTestFactory.CreateTask(projectId: projectId, prompt: "Task",
            completionPromise: "Promise", maxIterations: 5));
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _sut.GetByIdAsync(projectId);

        // Assert
        result.Should().NotBeNull();
        result!.Tasks.Should().BeEmpty("GetByIdAsync does not include tasks");
    }

    #endregion

    #region GetWithTasksAsync

    [Fact(Timeout = 5000)]
    public async SystemTask GetWithTasksAsync_WithValidId_ReturnsProjectWithTasks()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var project = IntegrationTestFactory.CreateProject(projectId, "Project", "Desc");
        _dbContext.Projects.Add(project);

        _dbContext.Tasks.Add(IntegrationTestFactory.CreateTask(projectId: projectId, prompt: "Task 1",
            completionPromise: "Promise 1", maxIterations: 5));
        _dbContext.Tasks.Add(IntegrationTestFactory.CreateTask(projectId: projectId, prompt: "Task 2",
            completionPromise: "Promise 2", maxIterations: 5));
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _sut.GetWithTasksAsync(projectId);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(projectId);
        result.Tasks.Should().HaveCount(2);
    }

    [Fact(Timeout = 5000)]
    public async SystemTask GetWithTasksAsync_WithNoTasks_ReturnsEmptyTaskList()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        _dbContext.Projects.Add(IntegrationTestFactory.CreateProject(projectId, "Empty Project", "No tasks"));
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _sut.GetWithTasksAsync(projectId);

        // Assert
        result.Should().NotBeNull();
        result!.Tasks.Should().BeEmpty();
    }

    [Fact(Timeout = 5000)]
    public async SystemTask GetWithTasksAsync_WithInvalidId_ReturnsNull()
    {
        // Act
        var result = await _sut.GetWithTasksAsync(Guid.NewGuid());

        // Assert
        result.Should().BeNull();
    }

    [Fact(Timeout = 5000)]
    public async SystemTask GetWithTasksAsync_MapsTaskFieldsCorrectly()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var project = IntegrationTestFactory.CreateProject(projectId, "Project", "Desc");
        _dbContext.Projects.Add(project);

        var taskId = Guid.NewGuid();
        _dbContext.Tasks.Add(IntegrationTestFactory.CreateTask(taskId, projectId: projectId, prompt: "Test prompt",
            completionPromise: "DONE", maxIterations: 10));
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _sut.GetWithTasksAsync(projectId);

        // Assert
        var taskDto = result!.Tasks[0];
        taskDto.Id.Should().Be(taskId);
        taskDto.Prompt.Should().Be("Test prompt");
        taskDto.CompletionPromise.Should().Be("DONE");
    }

    #endregion
}
