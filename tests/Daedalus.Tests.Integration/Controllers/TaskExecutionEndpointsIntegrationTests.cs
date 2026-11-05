using Daedalus.Api.Endpoints;
using Daedalus.Api.Services;
using Daedalus.Application.DTOs;
using Daedalus.Domain.Entities;
using Daedalus.Tests.Integration.Fixtures;
using Daedalus.Tests.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Controllers;

/// <summary>
///     Integration tests for TaskExecutionEndpoints handlers.
///     Tests execution data retrieval and filtering by task/session.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class TaskExecutionEndpointsIntegrationTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();

    private ITaskExecutionQueryService _executionQueryService = null!;

    public async Task InitializeAsync()
    {
        // Don't reset database - rely on fixture's respawn initialization
        // Just ensure our required project exists
        await StoreSeed.AddProjectIfAbsentAsync(
            fixture.Db, IntegrationTestFactory.CreateProject(IntegrationTestFactory.GetDefaultProjectId()));

        _executionQueryService = new TaskExecutionQueryService(fixture.Db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(Timeout = 5000)]
    public async Task GetByTaskId_WithValidTaskId_Returns200()
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

        await StoreSeed.AddTasksAsync(fixture.Db, task);
        await StoreSeed.AddExecutionsAsync(fixture.ConnectionString, execution);

        var (page, pageSize) = await PagingQueryBinding.BindAsync(new DefaultHttpContext(), string.Empty);

        // Act
        var result = await TaskExecutionEndpoints.GetByTaskId(
            taskId, page, pageSize, _executionQueryService, _loggerFactory, CancellationToken.None);

        // Assert
        // Red: return TypedResults.Ok on an empty PagedResultDto from GetByTaskId; the one execution is lost.
        var ok = result.Result.Should().BeOfType<Ok<PagedResultDto<TaskExecutionDto>>>().Subject;
        ok.Value!.Items.Should().HaveCount(1);
    }

    [Fact(Timeout = 5000)]
    public async Task GetBySessionId_WithValidSessionId_Returns200()
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

        await StoreSeed.AddTasksAsync(fixture.Db, task);
        await StoreSeed.AddExecutionsAsync(fixture.ConnectionString, execution);

        var (page, pageSize) = await PagingQueryBinding.BindAsync(new DefaultHttpContext(), string.Empty);

        // Act
        var result = await TaskExecutionEndpoints.GetBySessionId(
            sessionId, page, pageSize, _executionQueryService, _loggerFactory, CancellationToken.None);

        // Assert
        // Red: call GetByTaskIdAsync in place of GetBySessionIdAsync in GetBySessionId; no execution has this task id.
        var ok = result.Result.Should().BeOfType<Ok<PagedResultDto<TaskExecutionDto>>>().Subject;
        ok.Value!.Items.Should().HaveCount(1);
    }

    [Fact(Timeout = 5000)]
    public async Task GetByTaskId_WithNonexistentTaskId_Returns200WithEmptyList()
    {
        var (page, pageSize) = await PagingQueryBinding.BindAsync(new DefaultHttpContext(), string.Empty);

        // Act
        var result = await TaskExecutionEndpoints.GetByTaskId(
            Guid.NewGuid(), page, pageSize, _executionQueryService, _loggerFactory, CancellationToken.None);

        // Assert
        // Red: answer an ApiJsonResult 500 in place of the Ok for an unknown task in GetByTaskId.
        var ok = result.Result.Should().BeOfType<Ok<PagedResultDto<TaskExecutionDto>>>().Subject;
        ok.Value!.Items.Should().BeEmpty();
    }
}
