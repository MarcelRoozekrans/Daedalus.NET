using System.Reflection;
using Daedalus.Api.Endpoints;
using Daedalus.Api.Services;
using Daedalus.Application.DTOs;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Controllers;

/// <summary>
///     Integration tests for TaskExecutionEndpoints handlers.
///     Tests execution data retrieval and filtering by task/session.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class TaskExecutionsControllerIntegrationTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();

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
    }

    private static void PagingQuery(ApiQuery<int> page, ApiQuery<int> pageSize) => _ = (page, pageSize);

    /// <summary>Binds the two paging parameters as the endpoint does, from <paramref name="queryString"/>.</summary>
    private static async Task<(ApiQuery<int> Page, ApiQuery<int> PageSize)> BindPaging(string queryString = "")
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(queryString);
        var parameters = typeof(TaskExecutionsControllerIntegrationTests)
            .GetMethod(nameof(PagingQuery), BindingFlags.NonPublic | BindingFlags.Static)!.GetParameters();
        return (await Bind<ApiQuery<int>>(context, parameters[0]), await Bind<ApiQuery<int>>(context, parameters[1]));
    }

    private static async Task<T> Bind<T>(HttpContext context, ParameterInfo parameter)
        where T : class, IBindableFromHttpContext<T> => (await T.BindAsync(context, parameter))!;

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

        _dbContext.Tasks.Add(task);
        _dbContext.TaskExecutions.Add(execution);
        await _dbContext.SaveChangesAsync();

        var (page, pageSize) = await BindPaging();

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

        _dbContext.Tasks.Add(task);
        _dbContext.TaskExecutions.Add(execution);
        await _dbContext.SaveChangesAsync();

        var (page, pageSize) = await BindPaging();

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
        var (page, pageSize) = await BindPaging();

        // Act
        var result = await TaskExecutionEndpoints.GetByTaskId(
            Guid.NewGuid(), page, pageSize, _executionQueryService, _loggerFactory, CancellationToken.None);

        // Assert
        // Red: answer a JsonHttpResult 500 in place of the Ok for an unknown task in GetByTaskId.
        var ok = result.Result.Should().BeOfType<Ok<PagedResultDto<TaskExecutionDto>>>().Subject;
        ok.Value!.Items.Should().BeEmpty();
    }
}
