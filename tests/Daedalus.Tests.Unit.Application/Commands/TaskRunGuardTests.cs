using Daedalus.Application.Abstractions;
using Daedalus.Application.Commands.DeleteTask;
using Daedalus.Application.Commands.UpdateTask;
using Daedalus.Application.Services;

namespace Daedalus.Tests.Unit.Application.Commands;

/// <summary>
///     Phase 2.8, amendment A7: update and delete guard on the run, not the stored status, which reads Pending while a
///     run is live. See ruling P4.
/// </summary>
public sealed class TaskRunGuardTests
{
    private readonly ITaskRepository _tasks = Substitute.For<ITaskRepository>();
    private readonly IWorkflowRunStatusReader _runs = Substitute.For<IWorkflowRunStatusReader>();

    private DomainTask Given(WorkflowRunState? state)
    {
        var task = ApplicationTestFactory.CreateTask();
        if (state is { } s)
        {
            var runId = Guid.NewGuid();
            task.AttachRun(runId);
            _runs.ReadAsync(runId, Arg.Any<CancellationToken>()).Returns(new ValueTask<WorkflowRunStatus>(new WorkflowRunStatus(s, null)));
        }

        _tasks.GetByIdAsync(task.Id, Arg.Any<CancellationToken>()).Returns(Result<DomainTask>.Success(task));
        _tasks.UpdateAsync(default!, default).ReturnsForAnyArgs(Result.Success());
        _tasks.DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        return task;
    }

    private static UpdateTaskCommand Rename(Guid id) => new(id, "Renamed", null, null, null, null, null, null, null, null);

    /// <summary>Red: guard on the stored status again; the update succeeds while the run is live.</summary>
    [Theory]
    [InlineData(WorkflowRunState.Running)]
    [InlineData(WorkflowRunState.Awaiting)]
    public async Task An_update_while_the_run_is_live_is_refused_with_the_live_run_error(WorkflowRunState state)
    {
        var task = Given(state);

        var result = await new UpdateTaskCommandHandler(_tasks, _runs).Handle(Rename(task.Id), CancellationToken.None);

        result.Error.Should().StartWith(TaskRunGuard.LiveRunPrefix);
        await _tasks.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default);
    }

    /// <summary>Red: refuse any task that has a run; the pending task without one is refused.</summary>
    [Fact]
    public async Task A_pending_task_without_a_run_can_still_be_updated()
    {
        var task = Given(state: null);

        (await new UpdateTaskCommandHandler(_tasks, _runs).Handle(Rename(task.Id), CancellationToken.None)).IsSuccess.Should().BeTrue();
    }

    /// <summary>Red: drop the delete guard; the delete runs while the run is live.</summary>
    [Fact]
    public async Task A_delete_while_the_run_is_live_is_refused_with_the_live_run_error()
    {
        var task = Given(WorkflowRunState.Awaiting);

        var result = await new DeleteTaskCommandHandler(_tasks, _runs).Handle(new DeleteTaskCommand(task.Id), CancellationToken.None);

        result.Error.Should().StartWith(TaskRunGuard.LiveRunPrefix);
        await _tasks.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     A stored InProgress task with no run is an orphaned claim of the retired loop, and can be deleted (ruling P4).
    ///     Red: keep the old <c>task.Status is TaskStatus.InProgress</c> refusal.
    /// </summary>
    [Fact]
    public async Task An_orphaned_in_progress_task_without_a_run_can_be_deleted()
    {
        var task = Given(state: null);
        typeof(DomainTask).GetProperty(nameof(DomainTask.Status))!.SetValue(task, Daedalus.Domain.Entities.TaskStatus.InProgress);

        (await new DeleteTaskCommandHandler(_tasks, _runs).Handle(new DeleteTaskCommand(task.Id), CancellationToken.None)).IsSuccess.Should().BeTrue();
    }
}
