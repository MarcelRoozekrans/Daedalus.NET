using Daedalus.Agents;
using Daedalus.Agents.Workflow;
using Daedalus.Api.Services;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Services;
using Daedalus.Domain.Entities;
using Thalos.Workflow;
using ZeroAlloc.Results;
using DomainTask = Daedalus.Domain.Entities.Task;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Unit.Controllers;

/// <summary>
///     Phase 2.8 spec §2: starting a task's run. Each refusal happens before anything is spent: the starter is not called
///     and nothing is attached. Each assertion names the change that turns it red.
/// </summary>
public sealed class TaskManufactureServiceTests
{
    private const string Sandbox = "https://github.com/MarcelRoozekrans/daedalus-sandbox.git";
    private static readonly RunPrincipal Starter = new("a-dev", ["developer"]);

    private readonly ITaskRepository _tasks = Substitute.For<ITaskRepository>();
    private readonly IProjectRepository _projects = Substitute.For<IProjectRepository>();
    private readonly IWorkflowRunStatusReader _runs = Substitute.For<IWorkflowRunStatusReader>();
    private readonly IManufactureRunStarter _starter = Substitute.For<IManufactureRunStarter>();
    private readonly WorkflowConfig _workflow = new();
    private readonly Guid _started = Guid.NewGuid();

    public TaskManufactureServiceTests()
    {
        _workflow.Repositories.Add(new RepositoryConfig { Name = "daedalus-sandbox", Remote = Sandbox });
        _tasks.UpdateAsync(default!, default).ReturnsForAnyArgs(Result.Success());
        _runs.ReadAsync(Guid.Empty, default).ReturnsForAnyArgs(new ValueTask<WorkflowRunStatus>(WorkflowRunStatus.Unknown));
        _starter.StartAsync(default!, default).ReturnsForAnyArgs(
            new ValueTask<Result<Guid, ManufactureStartFailure>>(Result<Guid, ManufactureStartFailure>.Success(_started)));
    }

    private TaskManufactureService Service() => new(_tasks, _projects, _runs, _starter, _workflow);

    /// <summary>A task on a project with <paramref name="repositoryUrl"/>, alone in its project.</summary>
    private DomainTask Given(string repositoryUrl = Sandbox) => GivenInProject(Guid.NewGuid(), repositoryUrl);

    /// <summary>A task on project <paramref name="projectId"/>, beside <paramref name="siblings"/>, which must carry that project id.</summary>
    private DomainTask GivenInProject(Guid projectId, params DomainTask[] siblings) => GivenInProject(projectId, Sandbox, siblings);

    private DomainTask GivenInProject(Guid projectId, string repositoryUrl, params DomainTask[] siblings)
    {
        var project = Project.Create(projectId, "Sandbox", "The sandbox", repositoryUrl: repositoryUrl).Value;
        var task = NewTask(projectId, "TASK-002");
        _projects.GetByIdAsync(projectId, Arg.Any<CancellationToken>()).Returns(Result<Project>.Success(project));
        _tasks.GetByIdAsync(task.Id, Arg.Any<CancellationToken>()).Returns(Result<DomainTask>.Success(task));
        _tasks.GetByProjectIdAsync(projectId, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<DomainTask>>.Success([task, .. siblings]));
        return task;
    }

    /// <summary>The one place a task is built, so the <c>Task.Create</c> signature change in Task 11 touches one line.</summary>
    private static DomainTask NewTask(Guid projectId, string taskId) =>
        DomainTask.Create(Guid.NewGuid(), projectId, taskId, "Add a health check", "Expose GET /health.", Priority.Medium,
            "Backend", 1, Complexity.Medium, "Implement it.", "DONE", 10).Value;

    private void RunIs(DomainTask task, WorkflowRunState state)
    {
        var runId = Guid.NewGuid();
        task.AttachRun(runId);
        _runs.ReadAsync(runId, Arg.Any<CancellationToken>()).Returns(new ValueTask<WorkflowRunStatus>(new WorkflowRunStatus(state, null)));
    }

    private async Task NothingWasSpentAsync()
    {
        await _starter.DidNotReceiveWithAnyArgs().StartAsync(default!, default);
        await _tasks.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default);
    }

    /// <summary>Red: map every repository failure to <c>Failed</c>; the kind is not TaskNotFound.</summary>
    [Fact]
    public async Task A_missing_task_is_not_found_and_nothing_is_spent()
    {
        _tasks.GetByIdAsync(Guid.Empty, default).ReturnsForAnyArgs(Result<DomainTask>.Failure("Task 1 not found"));

        var result = await Service().StartAsync(Guid.NewGuid(), Starter, CancellationToken.None);

        result.Error.Kind.Should().Be(TaskManufactureFailureKind.TaskNotFound);
        await NothingWasSpentAsync();
    }

    /// <summary>Red: fall back to the first allow-listed entry when none matches; the start proceeds.</summary>
    [Fact]
    public async Task A_project_on_a_repository_that_is_not_allow_listed_is_refused_naming_its_url()
    {
        var task = Given("https://github.com/someone/else");

        var result = await Service().StartAsync(task.Id, Starter, CancellationToken.None);

        result.Error.Kind.Should().Be(TaskManufactureFailureKind.RepositoryNotAllowed);
        result.Error.Message.Should().Contain("https://github.com/someone/else");
        await NothingWasSpentAsync();
    }

    /// <summary>
    ///     Red: compare owner and name with <c>Ordinal</c>; the SSH form in another case does not match.
    ///     Red: send the remote URL instead of the entry's name; the repository assertion fails.
    /// </summary>
    [Fact]
    public async Task The_github_match_is_on_owner_and_name_ignoring_case_and_form()
    {
        var task = Given("git@github.com:marcelroozekrans/Daedalus-Sandbox.git");

        (await Service().StartAsync(task.Id, Starter, CancellationToken.None)).IsSuccess.Should().BeTrue();

        await _starter.Received(1).StartAsync(Arg.Is<ManufactureStartRequest>(r => r.Repository == "daedalus-sandbox"), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     Ruling P2: a non-GitHub remote matches its own exact string, and an unparsable entry is skipped, never thrown on.
    ///     Red: drop the exact-remote fallback; the local row is refused.
    ///     Red: read <c>.Value</c> of a failed parse; the method throws.
    /// </summary>
    [Theory]
    [InlineData("/srv/git/sandbox.git", "/srv/git/sandbox/", true)]
    [InlineData("/srv/git/sandbox.git", "https://github.com/MarcelRoozekrans/daedalus-sandbox", false)]
    [InlineData("/srv/git/Sandbox.git", "/srv/git/sandbox.git", false)]
    public void A_non_github_remote_matches_only_itself(string configured, string project, bool matches)
    {
        var entry = new RepositoryConfig { Name = "local", Remote = configured };

        (TaskManufactureService.MatchRepository(project, [entry]) is not null).Should().Be(matches);
    }

    /// <summary>Red: skip the dependency check; the start proceeds.</summary>
    [Fact]
    public async Task A_dependency_that_is_not_completed_is_refused_naming_it()
    {
        var projectId = Guid.NewGuid();
        var dependency = NewTask(projectId, "TASK-001");
        RunIs(dependency, WorkflowRunState.Running);
        var task = GivenInProject(projectId, dependency);
        task.AddDependency("TASK-001");

        var result = await Service().StartAsync(task.Id, Starter, CancellationToken.None);

        result.Error.Kind.Should().Be(TaskManufactureFailureKind.DependencyNotCompleted);
        result.Error.Message.Should().Contain("TASK-001").And.Contain("InProgress");
        await NothingWasSpentAsync();
    }

    /// <summary>
    ///     The dependency check reads the derived status: a dependency whose run succeeded is Completed, though it still
    ///     stores Pending. Red: check <c>dependency.Status</c>; the start is refused.
    /// </summary>
    [Fact]
    public async Task A_dependency_whose_run_succeeded_does_not_block()
    {
        var projectId = Guid.NewGuid();
        var dependency = NewTask(projectId, "TASK-001");
        RunIs(dependency, WorkflowRunState.Succeeded);
        var task = GivenInProject(projectId, dependency);
        task.AddDependency("TASK-001");

        (await Service().StartAsync(task.Id, Starter, CancellationToken.None)).IsSuccess.Should().BeTrue();
    }

    /// <summary>Red: skip a dependency that is not in the project; the start proceeds.</summary>
    [Fact]
    public async Task A_dependency_that_does_not_exist_is_refused_naming_it()
    {
        var task = Given();
        task.AddDependency("TASK-404");

        var result = await Service().StartAsync(task.Id, Starter, CancellationToken.None);

        result.Error.Kind.Should().Be(TaskManufactureFailureKind.DependencyNotCompleted);
        result.Error.Message.Should().Contain("TASK-404");
    }

    /// <summary>Red per row: treat that state as terminal; a second run starts.</summary>
    [Theory]
    [InlineData(WorkflowRunState.Running)]
    [InlineData(WorkflowRunState.Awaiting)]
    public async Task A_task_whose_run_is_live_is_a_conflict(WorkflowRunState state)
    {
        var task = Given();
        RunIs(task, state);

        var result = await Service().StartAsync(task.Id, Starter, CancellationToken.None);

        result.Error.Kind.Should().Be(TaskManufactureFailureKind.RunLive);
        await NothingWasSpentAsync();
    }

    /// <summary>
    ///     Spec §2: starting again replaces the id; earlier runs stay in the run history.
    ///     Red: refuse any task that already has a run.
    /// </summary>
    [Fact]
    public async Task A_task_whose_run_failed_starts_again_and_the_new_run_replaces_the_old()
    {
        var task = Given();
        RunIs(task, WorkflowRunState.Failed);

        (await Service().StartAsync(task.Id, Starter, CancellationToken.None)).Value.Should().Be(_started);

        task.WorkflowRunId.Should().Be(_started);
    }

    /// <summary>
    ///     Red: join title and description with one newline, or swap them; the intent assertion fails.
    ///     Red: pass another principal; the starter assertion fails.
    ///     Red: skip <c>UpdateAsync</c>; the attach assertion fails.
    /// </summary>
    [Fact]
    public async Task A_startable_task_starts_with_its_composed_intent_and_attaches_the_run()
    {
        var task = Given();

        var result = await Service().StartAsync(task.Id, Starter, CancellationToken.None);

        result.Value.Should().Be(_started);
        await _starter.Received(1).StartAsync(
            Arg.Is<ManufactureStartRequest>(r =>
                r.WorkIntent == "Add a health check\n\nExpose GET /health." && r.Repository == "daedalus-sandbox" && r.StartedBy == Starter),
            Arg.Any<CancellationToken>());
        await _tasks.Received(1).UpdateAsync(Arg.Is<DomainTask>(t => t.Id == task.Id && t.WorkflowRunId == _started), Arg.Any<CancellationToken>());
    }

    /// <summary>Red: attach before starting; <c>UpdateAsync</c> is received although the start failed.</summary>
    [Fact]
    public async Task A_starter_failure_is_carried_through_and_nothing_is_attached()
    {
        var task = Given();
        var failure = new ManufactureStartFailure(ManufactureStartFailureKind.Unavailable, "sandbox down");
        _starter.StartAsync(default!, default).ReturnsForAnyArgs(
            new ValueTask<Result<Guid, ManufactureStartFailure>>(Result<Guid, ManufactureStartFailure>.Failure(failure)));

        var result = await Service().StartAsync(task.Id, Starter, CancellationToken.None);

        result.Error.Kind.Should().Be(TaskManufactureFailureKind.StartFailed);
        result.Error.Start.Should().Be(failure);
        await _tasks.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default);
    }

    /// <summary>
    ///     The attach is saved on the instance that was read, so a task changed in between refuses it. The run already
    ///     exists, so the start answers AttachConflict naming it, and no second run is started.
    ///     Red: map every save failure to AttachFailed; the kind assertion fails.
    ///     Red: leave the run id off the failure; the run id assertion fails.
    /// </summary>
    [Fact]
    public async Task An_attach_that_loses_the_row_version_race_is_a_conflict_naming_the_run_that_started()
    {
        var task = Given();
        _tasks.UpdateAsync(default!, default).ReturnsForAnyArgs(Result.Failure(TaskRunGuard.ChangedUnderneath(task.Id)));

        var result = await Service().StartAsync(task.Id, Starter, CancellationToken.None);

        result.Error.Kind.Should().Be(TaskManufactureFailureKind.AttachConflict);
        result.Error.RunId.Should().Be(_started);
        result.Error.Message.Should().Contain(_started.ToString());
        await _starter.ReceivedWithAnyArgs(1).StartAsync(default!, default);
    }

    /// <summary>Red: map a save failure that is not a lost race to AttachConflict; the kind assertion fails.</summary>
    [Fact]
    public async Task An_attach_whose_save_fails_otherwise_is_attach_failed_naming_the_run()
    {
        var task = Given();
        _tasks.UpdateAsync(default!, default).ReturnsForAnyArgs(Result.Failure("Error updating task: the database is down"));

        var result = await Service().StartAsync(task.Id, Starter, CancellationToken.None);

        result.Error.Kind.Should().Be(TaskManufactureFailureKind.AttachFailed);
        result.Error.RunId.Should().Be(_started);
    }
}
