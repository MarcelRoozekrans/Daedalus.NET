using System.Security.Claims;
using Daedalus.Agents;
using Daedalus.Agents.Workflow;
using Daedalus.Api.Endpoints;
using Daedalus.Api.Services;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs.WorkflowRuns;
using Daedalus.Application.Services;
using Daedalus.Domain.Entities;
using Daedalus.Tests.Unit.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Thalos.Workflow;
using ZeroAlloc.Results;
using DomainTask = Daedalus.Domain.Entities.Task;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Unit.Endpoints;

/// <summary>The status each manufacture outcome answers with, through the real service over substitutes.</summary>
/// <remarks>
///     The controller's test of the action's <c>WorkflowResume</c> policy, a reflection over its
///     <c>[Authorize]</c>, is not ported: <c>EndpointContractSnapshotTests</c> line
///     <c>POST api/tasks/{id:guid}/manufacture | auth WorkflowResume,authenticated</c> pins the route's policy (R51).
/// </remarks>
public sealed class TasksEndpointsManufactureTests : IAsyncLifetime
{
    private readonly ITaskRepository _tasks = Substitute.For<ITaskRepository>();
    private readonly IProjectRepository _projects = Substitute.For<IProjectRepository>();
    private readonly IWorkflowRunStatusReader _runs = Substitute.For<IWorkflowRunStatusReader>();
    private readonly IManufactureRunStarter _starter = Substitute.For<IManufactureRunStarter>();
    private readonly WorkflowConfig _workflow = new();
    private readonly IWorkflowStore _store = Substitute.For<IWorkflowStore>();
    private readonly TaskManufactureServiceTests.CapturingLogger<TaskManufactureService> _logger = new();
    private readonly TaskManufactureServiceTests.CapturingLogger<WorkflowRunCanceller> _cancelLog = new();
    private readonly ServiceProvider _services = EndpointTestContext.Services();
    private readonly DefaultHttpContext _context;

    public TasksEndpointsManufactureTests()
    {
        _workflow.Repositories.Add(new RepositoryConfig { Name = "daedalus-sandbox", Remote = "https://github.com/o/daedalus-sandbox" });
        _runs.ReadAsync(Guid.Empty, default).ReturnsForAnyArgs(new ValueTask<WorkflowRunStatus>(WorkflowRunStatus.Unknown));
        _tasks.UpdateAsync(default!, default).ReturnsForAnyArgs(Result.Success());
        var identity = new ClaimsIdentity([new Claim("sub", "u-dev"), new Claim(ClaimTypes.Role, "developer")], authenticationType: "test");
        _context = EndpointTestContext.Create(_services);
        _context.User = new ClaimsPrincipal(identity);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _services.DisposeAsync();

    private TaskManufactureService Service() =>
        new(_tasks, _projects, _runs, _starter, _workflow,
            new WorkflowRunCanceller(TaskManufactureServiceTests.Gateway(_store), TimeProvider.System, _cancelLog), TimeProvider.System, _logger);

    private Task<Results<Created<StartWorkflowRunResponse>, ApiProblemResult>> Manufacture(Guid id) =>
        TasksEndpoints.Manufacture(id, _context, Service(), CancellationToken.None);

    private DomainTask Given(string repositoryUrl, WorkflowRunState? run = null)
    {
        var project = Project.Create(Guid.NewGuid(), "P", "D", repositoryUrl: repositoryUrl).Value;
        var task = DomainTask.Create(Guid.NewGuid(), project.Id, "TASK-1", "T", "D", Priority.Medium, "Backend", 1,
            Complexity.Medium, "P").Value;
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

    private static int StatusOf(Results<Created<StartWorkflowRunResponse>, ApiProblemResult> result) =>
        result.Result.Should().BeOfType<ApiProblemResult>().Subject.StatusCode;

    [Fact]
    public async Task A_missing_task_is_404()
    {
        _tasks.GetByIdAsync(Guid.Empty, default).ReturnsForAnyArgs(Result<DomainTask>.Failure("Task x not found"));

        // Red: StatusCodes.Status400BadRequest in place of Status404NotFound in Manufacture's TaskNotFound case.
        StatusOf(await Manufacture(Guid.NewGuid())).Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task A_repository_that_is_not_allow_listed_is_422()
    {
        var task = Given("https://github.com/o/other");

        // Red: StatusCodes.Status400BadRequest in place of Status422UnprocessableEntity in Manufacture's
        //      RepositoryNotAllowed or DependencyNotCompleted case.
        StatusOf(await Manufacture(task.Id)).Should().Be(StatusCodes.Status422UnprocessableEntity);
    }

    [Fact]
    public async Task A_live_run_is_409()
    {
        var task = Given("https://github.com/o/daedalus-sandbox", WorkflowRunState.Awaiting);

        // Red: StatusCodes.Status422UnprocessableEntity in place of Status409Conflict in Manufacture's RunLive case.
        StatusOf(await Manufacture(task.Id)).Should().Be(StatusCodes.Status409Conflict);
    }

    /// <summary>A starter failure maps as <c>POST /api/workflow-runs</c> maps it.</summary>
    [Fact]
    public async Task An_unavailable_starter_is_503_with_retry_after()
    {
        var task = Given("https://github.com/o/daedalus-sandbox");
        _starter.StartAsync(default!, default).ReturnsForAnyArgs(new ValueTask<Result<Guid, ManufactureStartFailure>>(
            Result<Guid, ManufactureStartFailure>.Failure(new ManufactureStartFailure(ManufactureStartFailureKind.Unavailable, "down"))));

        // Red: ApiProblem.Of(context, StatusCodes.Status500InternalServerError, start.Message) in place of
        //      ManufactureStartProblem.From in Manufacture's StartFailed case; the status and the header differ.
        StatusOf(await Manufacture(task.Id)).Should().Be(StatusCodes.Status503ServiceUnavailable);
        _context.Response.Headers.RetryAfter.ToString().Should().Be("30");
    }

    [Fact]
    public async Task A_started_run_is_201_with_the_run_id_and_the_run_location()
    {
        var task = Given("https://github.com/o/daedalus-sandbox");
        var runId = Guid.NewGuid();
        _starter.StartAsync(default!, default).ReturnsForAnyArgs(new ValueTask<Result<Guid, ManufactureStartFailure>>(
            Result<Guid, ManufactureStartFailure>.Success(runId)));

        var created = (await Manufacture(task.Id)).Result.Should().BeOfType<Created<StartWorkflowRunResponse>>().Subject;

        // Red: $"/api/tasks/{id}" in place of $"/api/workflow-runs/{result.Value}" in Manufacture's Created.
        created.Location.Should().Be($"/api/workflow-runs/{runId}");
        // Red: new StartWorkflowRunResponse(id) in place of new StartWorkflowRunResponse(result.Value) in Manufacture.
        created.Value.Should().Be(new StartWorkflowRunResponse(runId));
    }

    [Fact]
    public async Task A_dependency_that_is_not_completed_is_422()
    {
        var task = Given("https://github.com/o/daedalus-sandbox");
        task.AddDependency("TASK-0");
        _tasks.GetByProjectIdAsync(task.ProjectId, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<DomainTask>>.Success([task]));

        // Red: StatusCodes.Status409Conflict in place of Status422UnprocessableEntity in Manufacture's
        //      RepositoryNotAllowed or DependencyNotCompleted case.
        StatusOf(await Manufacture(task.Id)).Should().Be(StatusCodes.Status422UnprocessableEntity);
    }

    private DomainTask GivenStartedRun(Guid runId, string saveError)
    {
        var task = Given("https://github.com/o/daedalus-sandbox");
        _starter.StartAsync(default!, default).ReturnsForAnyArgs(new ValueTask<Result<Guid, ManufactureStartFailure>>(
            Result<Guid, ManufactureStartFailure>.Success(runId)));
        _tasks.UpdateAsync(default!, default).ReturnsForAnyArgs(Result.Failure(saveError));
        return task;
    }

    private static ProblemDetails ProblemOf(Results<Created<StartWorkflowRunResponse>, ApiProblemResult> result) =>
        result.Result.Should().BeOfType<ApiProblemResult>().Subject.ProblemDetails;

    /// <summary>
    ///     The run started, but the task changed underneath and the attach lost the race: 409, the run cancelled, the body
    ///     naming the run and the cancel outcome, and a Warning with both ids so the run can be found.
    /// </summary>
    [Fact]
    public async Task An_attach_that_loses_the_race_is_409_cancels_the_run_and_logs_both_ids()
    {
        var runId = Guid.NewGuid();
        var task = GivenStartedRun(runId, TaskRunGuard.ChangedUnderneath(Guid.NewGuid()));

        var result = await Manufacture(task.Id);

        // Red: StatusCodes.Status500InternalServerError in place of Status409Conflict in Manufacture's AttachConflict case.
        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        // Red: runId.ToString() in place of runId as the runId extension in UnattachedRunProblem.
        ProblemOf(result).Extensions["runId"].Should().Be(runId);
        // Red: !failure.RunCancelled in place of failure.RunCancelled as the runCancelled extension in UnattachedRunProblem.
        ProblemOf(result).Extensions["runCancelled"].Should().Be(true);
        // Red: _ = runId; in place of LogAttachLostRace in TaskManufactureService.StartAsync.
        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Message.Should().Contain(runId.ToString()).And.Contain(task.Id.ToString());
    }

    /// <summary>
    ///     A cancel that throws is best-effort: still 409, the body says the run was not cancelled, the service logs an Error
    ///     with both ids, and the canceller logs the exception.
    ///     Red: let the exception escape <c>WorkflowRunCanceller.CancelAsync</c>, applied as
    ///     <c>when (ex is not InvalidOperationException)</c> on its first <c>catch (Exception ex)</c>; the handler throws.
    /// </summary>
    [Fact]
    public async Task A_cancel_that_throws_still_answers_409_and_logs_an_error()
    {
        var runId = Guid.NewGuid();
        var task = GivenStartedRun(runId, TaskRunGuard.ChangedUnderneath(Guid.NewGuid()));
        _store.CancelAsync(Guid.Empty, default!, default).ReturnsForAnyArgs<ValueTask>(_ => throw new InvalidOperationException("store down"));

        var result = await Manufacture(task.Id);

        // Red: StatusCodes.Status500InternalServerError in place of Status409Conflict in Manufacture's AttachConflict case.
        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        // Red: true in place of failure.RunCancelled as the runCancelled extension in UnattachedRunProblem.
        ProblemOf(result).Extensions["runCancelled"].Should().Be(false);
        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error)
            .Which.Message.Should().Contain(runId.ToString()).And.Contain(task.Id.ToString());
        _cancelLog.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error)
            .Which.Exception.Should().BeOfType<InvalidOperationException>();
    }

    /// <summary>A save that fails for another reason: 500 naming the run, and an Error with both ids.</summary>
    [Fact]
    public async Task An_attach_whose_save_fails_is_500_naming_the_run_and_logs_both_ids()
    {
        var runId = Guid.NewGuid();
        var task = GivenStartedRun(runId, "Error updating task. The cause is logged.");

        var result = await Manufacture(task.Id);

        // Red: drop the AttachFailed case in Manufacture so it falls to the default; the status stays 500 but the
        //      runId extension is missing.
        StatusOf(result).Should().Be(StatusCodes.Status500InternalServerError);
        ProblemOf(result).Extensions.Should().ContainKey("runId").WhoseValue.Should().Be(runId);
        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error)
            .Which.Message.Should().Contain(runId.ToString()).And.Contain(task.Id.ToString());
    }

    /// <summary>
    ///     A repository's error text never reaches the 500 body.
    ///     Red: return the repository's <c>error</c> in place of <c>Generic(taskId)</c> in
    ///     <c>TaskManufactureService.ReadFailed</c>; the detail carries the secret.
    /// </summary>
    [Fact]
    public async Task A_read_failure_is_500_without_the_repository_error()
    {
        var task = Given("https://github.com/o/daedalus-sandbox");
        _projects.GetByIdAsync(task.ProjectId, Arg.Any<CancellationToken>()).Returns(Result<Project>.Failure("Error: Password=hunter2"));

        var result = await Manufacture(task.Id);

        StatusOf(result).Should().Be(StatusCodes.Status500InternalServerError);
        ProblemOf(result).Detail.Should().NotContain("hunter2");
    }
}
