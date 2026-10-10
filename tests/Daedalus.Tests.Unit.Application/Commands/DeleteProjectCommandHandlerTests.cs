using Daedalus.Application.Abstractions;
using Daedalus.Application.Commands.DeleteProject;
using Daedalus.Application.Services;
using Daedalus.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Unit.Application.Commands;

/// <summary>
///     Amendment A7: deleting a project cascades to its tasks, so it is refused while any of them has a live run.
/// </summary>
public sealed class DeleteProjectCommandHandlerTests
{
    private readonly IProjectRepository _projects = Substitute.For<IProjectRepository>();
    private readonly ITaskRepository _tasks = Substitute.For<ITaskRepository>();
    private readonly IWorkflowRunStatusReader _runs = Substitute.For<IWorkflowRunStatusReader>();
    private readonly Project _project = Project.Create(Guid.NewGuid(), "P", "D").Value;

    public DeleteProjectCommandHandlerTests()
    {
        _projects.GetByIdAsync(_project.Id, Arg.Any<CancellationToken>()).Returns(Result<Project>.Success(_project));
        _projects.DeleteAsync(_project.Id, Arg.Any<CancellationToken>()).Returns(Result.Success());
    }

    private DeleteProjectCommandHandler Handler() =>
        new(_projects, _tasks, _runs, NullLogger<DeleteProjectCommandHandler>.Instance);

    private DomainTask TaskWithRun(WorkflowRunState? state)
    {
        var task = ApplicationTestFactory.CreateTask(projectId: _project.Id, taskId: $"TASK-{Guid.NewGuid():N}"[..12]);
        if (state is { } s)
        {
            var runId = Guid.NewGuid();
            task.AttachRun(runId);
            _runs.ReadAsync(runId, Arg.Any<CancellationToken>()).Returns(new ValueTask<WorkflowRunStatus>(new WorkflowRunStatus(s, null)));
        }

        return task;
    }

    private void GivenTasks(params DomainTask[] tasks) =>
        _tasks.GetByProjectIdAsync(_project.Id, Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<DomainTask>>.Success(tasks));

    /// <summary>
    ///     Red: drop the live-run loop from the handler; the delete goes through and the cascade removes a task mid-run.
    ///     The second task is the live one, so a loop that checks only the first task is red too.
    /// </summary>
    [Theory]
    [InlineData(WorkflowRunState.Running)]
    [InlineData(WorkflowRunState.Awaiting)]
    public async Task A_project_with_a_live_task_run_is_not_deleted(WorkflowRunState live)
    {
        GivenTasks(TaskWithRun(WorkflowRunState.Succeeded), TaskWithRun(live));

        var result = await Handler().Handle(new DeleteProjectCommand(_project.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        TaskRunGuard.IsConflict(result.Error).Should().BeTrue();
        await _projects.DidNotReceiveWithAnyArgs().DeleteAsync(Guid.Empty, CancellationToken.None);
    }

    /// <summary>Red: treat any run as live, or refuse any task that has a run; this project is not deleted.</summary>
    [Fact]
    public async Task A_project_whose_task_runs_are_over_is_deleted()
    {
        GivenTasks(TaskWithRun(null), TaskWithRun(WorkflowRunState.Failed), TaskWithRun(WorkflowRunState.Cancelled), TaskWithRun(WorkflowRunState.Unknown));

        var result = await Handler().Handle(new DeleteProjectCommand(_project.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _projects.Received(1).DeleteAsync(_project.Id, Arg.Any<CancellationToken>());
    }

    /// <summary>Red: ignore a failed task read and delete anyway; a live run could not be ruled out.</summary>
    [Fact]
    public async Task A_project_whose_tasks_cannot_be_read_is_not_deleted()
    {
        _tasks.GetByProjectIdAsync(_project.Id, Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<DomainTask>>.Failure("Error retrieving the tasks. The cause is logged."));

        var result = await Handler().Handle(new DeleteProjectCommand(_project.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        await _projects.DidNotReceiveWithAnyArgs().DeleteAsync(Guid.Empty, CancellationToken.None);
    }

    /// <summary>Red: put <c>ex.Message</c> back in the unexpected-error text; the exception's detail leaks to the caller.</summary>
    [Fact]
    public async Task An_unexpected_exception_returns_generic_text()
    {
        _projects.GetByIdAsync(_project.Id, Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("secret connection string"));

        var result = await Handler().Handle(new DeleteProjectCommand(_project.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().NotContain("secret");
        result.Error.Should().Contain("The cause is logged");
    }
}
