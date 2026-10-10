using System.Reflection;
using System.Security.Claims;
using Daedalus.Agents;
using Daedalus.Agents.Workflow;
using Daedalus.Api.Controllers;
using Daedalus.Api.Services;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Services;
using Daedalus.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Results;
using DomainTask = Daedalus.Domain.Entities.Task;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Unit.Controllers;

/// <summary>The status each manufacture outcome answers with, through the real service over substitutes.</summary>
public sealed class TasksControllerManufactureTests
{
    private readonly ITaskRepository _tasks = Substitute.For<ITaskRepository>();
    private readonly IProjectRepository _projects = Substitute.For<IProjectRepository>();
    private readonly IWorkflowRunStatusReader _runs = Substitute.For<IWorkflowRunStatusReader>();
    private readonly IManufactureRunStarter _starter = Substitute.For<IManufactureRunStarter>();
    private readonly WorkflowConfig _workflow = new();
    private readonly CapturingLogger _logger = new();
    private readonly TasksController _controller;

    public TasksControllerManufactureTests()
    {
        _workflow.Repositories.Add(new RepositoryConfig { Name = "daedalus-sandbox", Remote = "https://github.com/o/daedalus-sandbox" });
        _runs.ReadAsync(Guid.Empty, default).ReturnsForAnyArgs(new ValueTask<WorkflowRunStatus>(WorkflowRunStatus.Unknown));
        _tasks.UpdateAsync(default!, default).ReturnsForAnyArgs(Result.Success());
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMvcCore().AddApiExplorer();
        var identity = new ClaimsIdentity([new Claim("sub", "u-dev"), new Claim(ClaimTypes.Role, "developer")], authenticationType: "test");
        _controller = new TasksController(Substitute.For<ITaskQueryService>(), Substitute.For<IApplicationCommands>(), _logger)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider(), User = new ClaimsPrincipal(identity) },
            },
        };
    }

    private TaskManufactureService Service() => new(_tasks, _projects, _runs, _starter, _workflow);

    private DomainTask Given(string repositoryUrl, WorkflowRunState? run = null)
    {
        var project = Project.Create(Guid.NewGuid(), "P", "D", repositoryUrl: repositoryUrl).Value;
        var task = DomainTask.Create(Guid.NewGuid(), project.Id, "TASK-1", "T", "D", Priority.Medium, "Backend", 1,
            Complexity.Medium, "P", "DONE", 10).Value;
        if (run is { } state)
        {
            var runId = Guid.NewGuid();
            task.AttachRun(runId);
            _runs.ReadAsync(runId, Arg.Any<CancellationToken>()).Returns(new ValueTask<WorkflowRunStatus>(new WorkflowRunStatus(state, null)));
        }

        _projects.GetByIdAsync(project.Id, Arg.Any<CancellationToken>()).Returns(Result<Project>.Success(project));
        _tasks.GetByIdAsync(task.Id, Arg.Any<CancellationToken>()).Returns(Result<DomainTask>.Success(task));
        return task;
    }

    private static int StatusOf(IActionResult result) => result.Should().BeAssignableTo<ObjectResult>().Subject.StatusCode!.Value;

    /// <summary>Red: map TaskNotFound to 400.</summary>
    [Fact]
    public async Task A_missing_task_is_404()
    {
        _tasks.GetByIdAsync(Guid.Empty, default).ReturnsForAnyArgs(Result<DomainTask>.Failure("Task x not found"));

        StatusOf(await _controller.Manufacture(Guid.NewGuid(), Service())).Should().Be(StatusCodes.Status404NotFound);
    }

    /// <summary>Red: map RepositoryNotAllowed to 400.</summary>
    [Fact]
    public async Task A_repository_that_is_not_allow_listed_is_422()
    {
        var task = Given("https://github.com/o/other");

        StatusOf(await _controller.Manufacture(task.Id, Service())).Should().Be(StatusCodes.Status422UnprocessableEntity);
    }

    /// <summary>Red: map RunLive to 422.</summary>
    [Fact]
    public async Task A_live_run_is_409()
    {
        var task = Given("https://github.com/o/daedalus-sandbox", WorkflowRunState.Awaiting);

        StatusOf(await _controller.Manufacture(task.Id, Service())).Should().Be(StatusCodes.Status409Conflict);
    }

    /// <summary>
    ///     A starter failure maps as <c>POST /api/workflow-runs</c> maps it.
    ///     Red: answer StartFailed with a fixed 500; the status and the header differ.
    /// </summary>
    [Fact]
    public async Task An_unavailable_starter_is_503_with_retry_after()
    {
        var task = Given("https://github.com/o/daedalus-sandbox");
        _starter.StartAsync(default!, default).ReturnsForAnyArgs(new ValueTask<Result<Guid, ManufactureStartFailure>>(
            Result<Guid, ManufactureStartFailure>.Failure(new ManufactureStartFailure(ManufactureStartFailureKind.Unavailable, "down"))));

        StatusOf(await _controller.Manufacture(task.Id, Service())).Should().Be(StatusCodes.Status503ServiceUnavailable);
        _controller.Response.Headers.RetryAfter.ToString().Should().Be("30");
    }

    /// <summary>Red: answer 200 with the task, or a location under /api/tasks; the type or the location fails.</summary>
    [Fact]
    public async Task A_started_run_is_201_with_the_run_id_and_the_run_location()
    {
        var task = Given("https://github.com/o/daedalus-sandbox");
        var runId = Guid.NewGuid();
        _starter.StartAsync(default!, default).ReturnsForAnyArgs(new ValueTask<Result<Guid, ManufactureStartFailure>>(
            Result<Guid, ManufactureStartFailure>.Success(runId)));

        var created = (await _controller.Manufacture(task.Id, Service())).Should().BeOfType<CreatedResult>().Subject;

        created.Location.Should().Be($"/api/workflow-runs/{runId}");
        created.Value.Should().Be(new StartWorkflowRunResponse(runId));
    }

    /// <summary>Red: map DependencyNotCompleted to 409.</summary>
    [Fact]
    public async Task A_dependency_that_is_not_completed_is_422()
    {
        var task = Given("https://github.com/o/daedalus-sandbox");
        task.AddDependency("TASK-0");
        _tasks.GetByProjectIdAsync(task.ProjectId, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<DomainTask>>.Success([task]));

        StatusOf(await _controller.Manufacture(task.Id, Service())).Should().Be(StatusCodes.Status422UnprocessableEntity);
    }

    /// <summary>
    ///     The run started, but the task changed underneath and the attach lost the race: 409, logged at Warning with both
    ///     ids so the unattached run can be found.
    ///     Red: map AttachConflict to 500; the status assertion fails.
    ///     Red: drop the log call; the log assertion fails.
    /// </summary>
    [Fact]
    public async Task An_attach_that_loses_the_race_is_409_and_logs_both_ids()
    {
        var task = Given("https://github.com/o/daedalus-sandbox");
        var runId = Guid.NewGuid();
        _starter.StartAsync(default!, default).ReturnsForAnyArgs(new ValueTask<Result<Guid, ManufactureStartFailure>>(
            Result<Guid, ManufactureStartFailure>.Success(runId)));
        _tasks.UpdateAsync(default!, default).ReturnsForAnyArgs(Result.Failure(TaskRunGuard.ChangedUnderneath(task.Id)));

        StatusOf(await _controller.Manufacture(task.Id, Service())).Should().Be(StatusCodes.Status409Conflict);

        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Message.Should().Contain(runId.ToString()).And.Contain(task.Id.ToString());
    }

    /// <summary>
    ///     The action carries the same policy as <c>POST /api/workflow-runs</c>, not the class's default.
    ///     Red: change the action's policy to <c>TaskManagement</c>; the policy assertion fails.
    /// </summary>
    [Fact]
    public void The_action_requires_the_workflow_resume_policy()
    {
        var method = typeof(TasksController).GetMethod(nameof(TasksController.Manufacture))!;

        method.GetCustomAttribute<AuthorizeAttribute>()!.Policy.Should().Be("WorkflowResume");
    }

    /// <summary>Records each entry's level and formatted message.</summary>
    private sealed class CapturingLogger : ILogger<TasksController>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
