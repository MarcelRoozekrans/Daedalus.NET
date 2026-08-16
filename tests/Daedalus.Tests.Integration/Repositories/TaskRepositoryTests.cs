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
using TaskStatus = Daedalus.Domain.Entities.TaskStatus;

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
    public async SystemTask UpdateAsync_ChangingTaskStatus_ShouldPersist()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var project = Project.Create(projectId, "Test Project", "Test Description").Value;
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        var sessionId = Guid.NewGuid();
        var task = new TaskTestBuilder()
            .WithPrompt("Test prompt")
            .WithCompletionPromise("DONE")
            .WithMaxIterations(10)
            .ClaimedBy(sessionId)
            .WithProjectId(projectId)
            .Build();

        await _repository.AddAsync(task, CancellationToken.None);

        // Act
        var updateResult = await _repository.UpdateAsync(task, CancellationToken.None);
        var retrievedTask = await _repository.GetByIdAsync(task.Id, CancellationToken.None);

        // Assert
        updateResult.IsSuccess.Should().BeTrue();
        retrievedTask.IsSuccess.Should().BeTrue();
        retrievedTask.Value.Status.Should().Be(TaskStatus.InProgress);
        retrievedTask.Value.CurrentSessionId.Should().Be(sessionId);
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

        var task = new TaskTestBuilder().WithPrompt("Test prompt").WithCompletionPromise("DONE").WithMaxIterations(10)
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

        var task = new TaskTestBuilder().WithPrompt("Test prompt").WithCompletionPromise("DONE").WithMaxIterations(10)
            .WithProjectId(projectId)
            .Build();
        var sessionId = Guid.NewGuid();
        task.Claim(sessionId);

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
        await _repository.RecordExecutionAsync(execution, CancellationToken.None);

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
            .WithCompletionPromise("DONE")
            .WithMaxIterations(10)
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
            .WithCompletionPromise("DONE")
            .WithMaxIterations(5)
            .WithProjectId(projectId)
            .Build();

        var task2 = new TaskTestBuilder()
            .WithPrompt("Prompt 2")
            .WithCompletionPromise("DONE")
            .WithMaxIterations(10)
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

    #region RecordExecutionAsync Tests

    [Fact]
    public async SystemTask RecordExecutionAsync_WithValidExecution_ShouldSucceed()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var project = Project.Create(projectId, "Test Project", "Test Description").Value;
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        var task = new TaskTestBuilder().WithPrompt("Test prompt").WithCompletionPromise("DONE").WithMaxIterations(10)
            .WithProjectId(projectId)
            .Build();
        var sessionId = Guid.NewGuid();
        task.Claim(sessionId);
        await _repository.AddAsync(task, CancellationToken.None);

        var execution = new TaskExecution
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            SessionId = sessionId,
            IterationNumber = 1,
            Prompt = task.Prompt,
            LlmResponse = "Response",
            CompletionPromiseFound = false,
            ExecutionDuration = TimeSpan.FromMilliseconds(100)
        };

        // Act
        var result = await _repository.RecordExecutionAsync(execution, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();

        var retrievedTask = await _repository.GetByIdAsync(task.Id, CancellationToken.None);
        retrievedTask.Value.Executions.Should().HaveCount(1);
    }

    [Fact]
    public async SystemTask RecordExecutionAsync_MultipleExecutions_ShouldAllBePersisted()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var project = Project.Create(projectId, "Test Project", "Test Description").Value;
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        var task = new TaskTestBuilder().WithPrompt("Test prompt").WithCompletionPromise("DONE").WithMaxIterations(5)
            .WithProjectId(projectId)
            .Build();
        var sessionId = Guid.NewGuid();
        task.Claim(sessionId);
        await _repository.AddAsync(task, CancellationToken.None);

        // Act
        for (var i = 1; i <= 3; i++)
        {
            var execution = new TaskExecution
            {
                Id = Guid.NewGuid(),
                TaskId = task.Id,
                SessionId = sessionId,
                IterationNumber = i,
                Prompt = task.Prompt,
                LlmResponse = $"Response {i}",
                CompletionPromiseFound = false,
                ExecutionDuration = TimeSpan.FromMilliseconds(100 * i)
            };
            await _repository.RecordExecutionAsync(execution, CancellationToken.None);
        }

        // Assert
        var retrievedTask = await _repository.GetByIdAsync(task.Id, CancellationToken.None);
        retrievedTask.Value.Executions.Should().HaveCount(3);
        retrievedTask.Value.Executions[0].IterationNumber.Should().Be(1);
        retrievedTask.Value.Executions[1].IterationNumber.Should().Be(2);
        retrievedTask.Value.Executions[2].IterationNumber.Should().Be(3);
    }

    #endregion

    #region GetPendingAsync Tests

    [Fact]
    public async SystemTask GetPendingAsync_WithPendingTasks_ShouldReturnTasks()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var project = Project.Create(projectId, "Test Project", "Test Description").Value;
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        var task1 = new TaskTestBuilder().WithPrompt("Prompt 1").WithCompletionPromise("DONE").WithMaxIterations(10)
            .WithProjectId(projectId)
            .Build();
        var task2 = new TaskTestBuilder().WithPrompt("Prompt 2").WithCompletionPromise("DONE").WithMaxIterations(10)
            .WithProjectId(projectId)
            .Build();

        await _repository.AddAsync(task1, CancellationToken.None);
        await _repository.AddAsync(task2, CancellationToken.None);

        // Act
        var result = await _repository.GetPendingAsync(CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
    }

    [Fact]
    public async SystemTask GetPendingAsync_WithNoTasks_ShouldReturnEmptyList()
    {
        // Act
        var result = await _repository.GetPendingAsync(CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async SystemTask GetPendingAsync_ShouldNotReturnClaimedTasks()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var project = Project.Create(projectId, "Test Project", "Test Description").Value;
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        var pendingTask = new TaskTestBuilder()
            .WithPrompt("Pending")
            .WithCompletionPromise("DONE")
            .WithMaxIterations(10)
            .WithProjectId(projectId)
            .Build();
        var claimedTask = new TaskTestBuilder()
            .WithPrompt("Claimed")
            .WithCompletionPromise("DONE")
            .WithMaxIterations(10)
            .ClaimedBy(Guid.NewGuid())
            .WithProjectId(projectId)
            .Build();

        await _repository.AddAsync(pendingTask, CancellationToken.None);
        await _repository.AddAsync(claimedTask, CancellationToken.None);
        await _repository.UpdateAsync(claimedTask, CancellationToken.None);

        // Act
        var result = await _repository.GetPendingAsync(CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
        result.Value[0].Id.Should().Be(pendingTask.Id);
    }

    [Fact]
    public async SystemTask GetPendingAsync_ShouldReturnOrderedByCreatedAt()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var project = Project.Create(projectId, "Test Project", "Test Description").Value;
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        var oldTask = new TaskTestBuilder().WithPrompt("Old").WithCompletionPromise("DONE").WithMaxIterations(10)
            .WithProjectId(projectId)
            .Build();
        var newTask = new TaskTestBuilder().WithPrompt("New").WithCompletionPromise("DONE").WithMaxIterations(10)
            .WithProjectId(projectId)
            .Build();

        await _repository.AddAsync(oldTask, CancellationToken.None);
        await SystemTask.Delay(100);
        await _repository.AddAsync(newTask, CancellationToken.None);

        // Act
        var result = await _repository.GetPendingAsync(CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
        result.Value[0].Id.Should().Be(oldTask.Id);
        result.Value[1].Id.Should().Be(newTask.Id);
    }

    #endregion

    #region ClaimNextAsync Tests

    [Fact]
    public async SystemTask ClaimNextAsync_WithPendingTasks_ShouldClaimAndLock()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var project = Project.Create(projectId, "Test Project", "Test Description").Value;
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        var task = new TaskTestBuilder().WithPrompt("Test").WithCompletionPromise("DONE").WithMaxIterations(10)
            .WithProjectId(projectId).Build();
        await _repository.AddAsync(task, CancellationToken.None);

        var sessionId = Guid.NewGuid();

        // Act
        var result = await _repository.ClaimNextAsync(sessionId, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Status.Should().Be(TaskStatus.InProgress);
        result.Value.CurrentSessionId.Should().Be(sessionId);
    }

    [Fact]
    public async SystemTask ClaimNextAsync_WithNoPendingTasks_ShouldReturnNull()
    {
        // Arrange
        var sessionId = Guid.NewGuid();

        // Act
        var result = await _repository.ClaimNextAsync(sessionId, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public async SystemTask ClaimNextAsync_WithMultiplePendingTasks_ShouldClaimOldest()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var project = Project.Create(projectId, "Test Project", "Test Description").Value;
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        var task1 = new TaskTestBuilder().WithPrompt("First").WithCompletionPromise("DONE").WithMaxIterations(10)
            .WithProjectId(projectId)
            .Build();
        var task2 = new TaskTestBuilder().WithPrompt("Second").WithCompletionPromise("DONE").WithMaxIterations(10)
            .WithProjectId(projectId)
            .Build();

        await _repository.AddAsync(task1, CancellationToken.None);
        await SystemTask.Delay(100);
        await _repository.AddAsync(task2, CancellationToken.None);

        var sessionId = Guid.NewGuid();

        // Act
        var result = await _repository.ClaimNextAsync(sessionId, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value!.Id.Should().Be(task1.Id);
    }

    #endregion

    #region GetStaleInProgressAsync Tests

    [Fact]
    public async SystemTask GetStaleInProgressAsync_WithStaleSession_ShouldReturnTasks()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var project = Project.Create(projectId, "Test Project", "Test Description").Value;
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        var task = new TaskTestBuilder().WithPrompt("Stale Task").WithCompletionPromise("DONE").WithMaxIterations(10)
            .WithProjectId(projectId)
            .Build();
        var sessionId = Guid.NewGuid();
        task.Claim(sessionId);
        await _repository.AddAsync(task, CancellationToken.None);

        var session = ExecutionSession.Create(sessionId, "worker-1").Value;
        session.Heartbeat(DateTime.UtcNow.AddMinutes(-10));
        _dbContext.ExecutionSessions.Add(session);
        await _dbContext.SaveChangesAsync();

        var staleness = TimeSpan.FromMinutes(5);

        // Act
        var result = await _repository.GetStaleInProgressAsync(staleness, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
        result.Value[0].Id.Should().Be(task.Id);
    }

    [Fact]
    public async SystemTask GetStaleInProgressAsync_WithActiveSession_ShouldNotReturnTasks()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var project = Project.Create(projectId, "Test Project", "Test Description").Value;
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        var task = new TaskTestBuilder()
            .WithPrompt("Active Task")
            .WithCompletionPromise("DONE")
            .WithMaxIterations(10)
            .WithProjectId(projectId)
            .Build();
        var sessionId = Guid.NewGuid();
        task.Claim(sessionId);
        await _repository.AddAsync(task, CancellationToken.None);

        var session = ExecutionSession.Create(sessionId, "worker-1").Value;
        session.Heartbeat(DateTime.UtcNow);
        _dbContext.ExecutionSessions.Add(session);
        await _dbContext.SaveChangesAsync();

        var staleness = TimeSpan.FromMinutes(5);

        // Act
        var result = await _repository.GetStaleInProgressAsync(staleness, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async SystemTask GetStaleInProgressAsync_WithMultipleStaleTasks_ShouldReturnAll()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var project = Project.Create(projectId, "Test Project", "Test Description").Value;
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        var staleTasks = new List<Task>();
        var sessionId = Guid.NewGuid();

        for (var i = 0; i < 3; i++)
        {
            var task = new TaskTestBuilder()
                .WithPrompt($"Task {i}")
                .WithCompletionPromise("DONE")
                .WithMaxIterations(10)
                .ClaimedBy(sessionId)
                .WithProjectId(projectId)
                .Build();
            await _repository.AddAsync(task, CancellationToken.None);
            staleTasks.Add(task);
        }

        var session = ExecutionSession.Create(sessionId, "worker-1").Value;
        session.Heartbeat(DateTime.UtcNow.AddMinutes(-10));
        _dbContext.ExecutionSessions.Add(session);
        await _dbContext.SaveChangesAsync();

        var staleness = TimeSpan.FromMinutes(5);

        // Act
        var result = await _repository.GetStaleInProgressAsync(staleness, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(3);
    }

    #endregion
}
