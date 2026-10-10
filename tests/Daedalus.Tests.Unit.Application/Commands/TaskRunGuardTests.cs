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
        _tasks.DeleteAsync(Arg.Any<DomainTask>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
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

    /// <summary>
    ///     "Only Pending is editable" applies to the derived status: a stored-Pending task whose run is over is refused
    ///     with the non-live error, so the controller answers 400, not 409.
    ///     Red: revert the handler to <c>task.Status != Pending</c>; the stored Pending lets the update through.
    /// </summary>
    [Theory]
    [InlineData(WorkflowRunState.Succeeded)]
    [InlineData(WorkflowRunState.Failed)]
    [InlineData(WorkflowRunState.Cancelled)]
    public async Task An_update_of_a_task_whose_run_is_over_is_refused_but_not_as_a_conflict(WorkflowRunState state)
    {
        var task = Given(state);

        var result = await new UpdateTaskCommandHandler(_tasks, _runs).Handle(Rename(task.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().StartWith("Cannot update task: current status is");
        TaskRunGuard.IsConflict(result.Error).Should().BeFalse();
        await _tasks.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default);
    }

    /// <summary>
    ///     A save that lost a race with another write of the task is a conflict, with its own message.
    ///     Red: wrap the repository error as <c>Failed to update task: ...</c> again; the prefix is lost.
    /// </summary>
    [Fact]
    public async Task An_update_that_lost_a_race_keeps_the_conflict_error()
    {
        var task = Given(state: null);
        _tasks.UpdateAsync(default!, default).ReturnsForAnyArgs(Result.Failure(TaskRunGuard.ChangedUnderneath(task.Id)));

        var result = await new UpdateTaskCommandHandler(_tasks, _runs).Handle(Rename(task.Id), CancellationToken.None);

        TaskRunGuard.IsConflict(result.Error).Should().BeTrue();
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
        await _tasks.DidNotReceive().DeleteAsync(Arg.Any<DomainTask>(), Arg.Any<CancellationToken>());
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
