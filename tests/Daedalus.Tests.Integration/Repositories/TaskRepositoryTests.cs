using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Builders;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit.Sdk;
using Project = Daedalus.Domain.Entities.Project;
using SystemTask = System.Threading.Tasks.Task;
using Task = Daedalus.Domain.Entities.Task;

namespace Daedalus.Tests.Integration.Repositories;

/// <summary>
///     Integration tests for TaskRepository.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class TaskRepositoryTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly ILogger<TaskRepository> _logger = Substitute.For<ILogger<TaskRepository>>();
    private ApplicationDbContext _dbContext = null!;
    private TaskRepository _repository = null!;

    public async SystemTask InitializeAsync()
    {
        var options = PostgresFixture.CreateDbContextOptions(fixture.ConnectionString);

        _dbContext = new ApplicationDbContext(options);
        _repository = new TaskRepository(_dbContext, _logger);

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


    #region UpdateAsync Tests

    [Fact]
    public async SystemTask UpdateAsync_ChangingTaskMetadata_ShouldPersist()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var project = Project.Create(projectId, "Test Project", "Test Description").Value;
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        var task = new TaskTestBuilder()
            .WithPrompt("Test prompt")
            .WithProjectId(projectId)
            .Build();

        await _repository.AddAsync(task, CancellationToken.None);
        task.UpdateMetadata("Renamed", "New description", Priority.High, "Backend", Complexity.High)
            .IsSuccess.Should().BeTrue();

        // Act
        var updateResult = await _repository.UpdateAsync(task, CancellationToken.None);
        var retrievedTask = await _repository.GetByIdAsync(task.Id, CancellationToken.None);

        // Assert
        updateResult.IsSuccess.Should().BeTrue();
        retrievedTask.IsSuccess.Should().BeTrue();
        retrievedTask.Value.Title.Should().Be("Renamed");
        retrievedTask.Value.Priority.Should().Be(Priority.High);
        retrievedTask.Value.Phase.Should().Be("Backend");
    }

    #endregion

    #region GetByIdAsync Tests

    [Fact]
    public async SystemTask GetByIdAsync_WithExistingTask_ShouldReturnTask()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var project = Project.Create(projectId, "Test Project", "Test Description").Value;
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        var task = new TaskTestBuilder().WithPrompt("Test prompt")
            .WithProjectId(projectId)
            .Build();
        await _repository.AddAsync(task, CancellationToken.None);

        // Act
        var result = await _repository.GetByIdAsync(task.Id, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be(task.Id);
        result.Value.Prompt.Should().Be("Test prompt");
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
    public async SystemTask GetByIdAsync_ShouldIncludeExecutions()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var project = Project.Create(projectId, "Test Project", "Test Description").Value;
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        var task = new TaskTestBuilder().WithPrompt("Test prompt")
            .WithProjectId(projectId)
            .Build();
        var sessionId = Guid.NewGuid();

        var execution = new TaskExecution
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            SessionId = sessionId,
            IterationNumber = 1,
            Prompt = task.Prompt,
            LlmResponse = "Response 1",
            CompletionPromiseFound = false,
            ExecutionDuration = TimeSpan.FromMilliseconds(100)
        };

        await _repository.AddAsync(task, CancellationToken.None);
        _dbContext.TaskExecutions.Add(execution);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        // Act
        var result = await _repository.GetByIdAsync(task.Id, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Executions.Should().HaveCount(1);
        result.Value.Executions[0].IterationNumber.Should().Be(1);
    }

    #endregion

    #region AddAsync Tests

    [Fact]
    public async SystemTask AddAsync_WithValidTask_ShouldSucceed()
    {
        // Arrange - Create a project first (Task has FK to Project)
        var projectId = Guid.NewGuid();
        var project = Project.Create(projectId, "Test Project", "Test Description").Value;
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        var task = new TaskTestBuilder()
            .WithPrompt("Test prompt")
            .WithProjectId(projectId)
            .Build();

        // Act
        var result = await _repository.AddAsync(task, CancellationToken.None);

        // Assert
        if (result.IsFailure)
        {
            throw new XunitException($"AddAsync failed: {result.Error}");
        }

        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be(task.Id);

        var retrievedTask = await _repository.GetByIdAsync(task.Id, CancellationToken.None);
        retrievedTask.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async SystemTask AddAsync_MultipleTasksWithDifferentIds_ShouldSucceed()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var project = Project.Create(projectId, "Test Project", "Test Description").Value;
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        var task1 = new TaskTestBuilder()
            .WithPrompt("Prompt 1")
            .WithProjectId(projectId)
            .Build();

        var task2 = new TaskTestBuilder()
            .WithPrompt("Prompt 2")
            .WithProjectId(projectId)
            .Build();

        // Act
        var result1 = await _repository.AddAsync(task1, CancellationToken.None);
        var result2 = await _repository.AddAsync(task2, CancellationToken.None);

        // Assert
        result1.IsSuccess.Should().BeTrue();
        result2.IsSuccess.Should().BeTrue();
        result1.Value.Id.Should().NotBe(result2.Value.Id);
    }

    #endregion
}
