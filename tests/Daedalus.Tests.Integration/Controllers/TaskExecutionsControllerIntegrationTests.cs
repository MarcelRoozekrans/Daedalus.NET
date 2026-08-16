using Daedalus.Api.Controllers;
using Daedalus.Api.Services;
using Daedalus.Application.DTOs;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Controllers;

/// <summary>
///     Integration tests for TaskExecutionsController HTTP endpoints.
///     Tests execution data retrieval and filtering by task/session.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class TaskExecutionsControllerIntegrationTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly ILogger<TaskExecutionsController>
        _loggerMock = Substitute.For<ILogger<TaskExecutionsController>>();

    private TaskExecutionsController _controller = null!;
    private ApplicationDbContext _dbContext = null!;
    private ITaskExecutionQueryService _executionQueryService = null!;

    public async Task InitializeAsync()
    {
        var options = PostgresFixture.CreateDbContextOptions(fixture.ConnectionString);

        _dbContext = new ApplicationDbContext(options);

        // Don't reset database - rely on fixture's respawn initialization
        // Just ensure our required project exists
        var projectId = IntegrationTestFactory.GetDefaultProjectId();
        var existingProject = await _dbContext.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == projectId);
        if (existingProject == null)
        {
            var project = IntegrationTestFactory.CreateProject(projectId);
            _dbContext.Projects.Add(project);
            await _dbContext.SaveChangesAsync();
        }

        _executionQueryService = new TaskExecutionQueryService(_dbContext);
        _controller = new TaskExecutionsController(_executionQueryService, _loggerMock);
    }

    public async Task DisposeAsync()
    {
        if (_dbContext != null)
        {
            await _dbContext.DisposeAsync();
        }
    }

    [Fact(Timeout = 5000)]
    public async Task GetByTaskId_WithValidTaskId_Returns200()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var task = IntegrationTestFactory.CreateTask(taskId, prompt: "Test", completionPromise: "Promise",
            maxIterations: 5);

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
        _dbContext.TaskExecutions.Add(execution);
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _controller.GetByTaskId(taskId);

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        var okResult = (OkObjectResult)result;
        var pagedResult = okResult.Value as PagedResultDto<TaskExecutionDto>;
        pagedResult!.Items.Should().HaveCount(1);
    }

    [Fact(Timeout = 5000)]
    public async Task GetBySessionId_WithValidSessionId_Returns200()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var task = IntegrationTestFactory.CreateTask(taskId, prompt: "Test", completionPromise: "Promise",
            maxIterations: 5);

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
        _dbContext.TaskExecutions.Add(execution);
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _controller.GetBySessionId(sessionId);

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        var okResult = (OkObjectResult)result;
        var pagedResult = okResult.Value as PagedResultDto<TaskExecutionDto>;
        pagedResult!.Items.Should().HaveCount(1);
    }

    [Fact(Timeout = 5000)]
    public async Task GetByTaskId_WithNonexistentTaskId_Returns200WithEmptyList()
    {
        // Act
        var result = await _controller.GetByTaskId(Guid.NewGuid());

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        var okResult = (OkObjectResult)result;
        var pagedResult = okResult.Value as PagedResultDto<TaskExecutionDto>;
        pagedResult!.Items.Should().BeEmpty();
    }
}
