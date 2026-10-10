using Daedalus.Application.Abstractions;
using Daedalus.Application.Services;
using TaskStatus = Daedalus.Domain.Entities.TaskStatus;

namespace Daedalus.Tests.Unit.Application.Services;

/// <summary>Phase 2.8 spec §2: a task's status is its run's status, mapped on read.</summary>
public sealed class TaskStatusDerivationTests
{
    /// <summary>Red per row: map that run state to another task status in <c>Derive</c>.</summary>
    [Theory]
    [InlineData(WorkflowRunState.Running, TaskStatus.InProgress)]
    [InlineData(WorkflowRunState.Awaiting, TaskStatus.AwaitingApproval)]
    [InlineData(WorkflowRunState.Succeeded, TaskStatus.Completed)]
    [InlineData(WorkflowRunState.Failed, TaskStatus.Failed)]
    [InlineData(WorkflowRunState.Cancelled, TaskStatus.Cancelled)]
    public void A_task_with_a_run_shows_the_runs_status(WorkflowRunState state, TaskStatus expected)
    {
        var task = ApplicationTestFactory.CreateTask();
        task.AttachRun(Guid.NewGuid());

        TaskStatusDerivation.Derive(task, new WorkflowRunStatus(state, null)).Should().Be(expected);
    }

    /// <summary>
    ///     Unknown means the engine is off or the run does not exist; in both cases a task that does have a run id shows
    ///     its stored status, not a status guessed from the missing run.
    ///     Red: map <c>Unknown</c> to <c>Pending</c> in <c>Derive</c>; the stored <c>Failed</c> reads as Pending.
    /// </summary>
    [Fact]
    public void A_task_with_a_run_id_whose_run_cannot_be_read_keeps_its_stored_status()
    {
        var task = ApplicationTestFactory.CreateTask();
        task.AttachRun(Guid.NewGuid()).IsSuccess.Should().BeTrue();
        typeof(DomainTask).GetProperty(nameof(DomainTask.Status))!.SetValue(task, TaskStatus.Failed);

        TaskStatusDerivation.Derive(task, WorkflowRunStatus.Unknown).Should().Be(TaskStatus.Failed);
    }

    /// <summary>
    ///     A task from before phase 2.8 has no run, so it keeps what the loop stored. It is seeded by reflection
    ///     because no remaining domain method sets <c>Completed</c>.
    ///     Red: map Unknown to Pending in <c>Derive</c>; the stored value reads as Pending.
    /// </summary>
    [Theory]
    [InlineData(TaskStatus.Completed)]
    [InlineData(TaskStatus.Failed)]
    [InlineData(TaskStatus.Abandoned)]
    public void An_old_task_without_a_run_keeps_the_status_the_loop_stored(TaskStatus stored)
    {
        var task = ApplicationTestFactory.CreateTask();
        typeof(DomainTask).GetProperty(nameof(DomainTask.Status))!.SetValue(task, stored);

        TaskStatusDerivation.Derive(task, WorkflowRunStatus.Unknown).Should().Be(stored);
    }

    /// <summary>
    ///     A task without a run is never looked up.
    ///     Red: call <c>reader.ReadAsync(Guid.Empty, ct)</c> for a task without a run; the reader receives a call.
    /// </summary>
    [Fact]
    public async Task A_task_without_a_run_is_not_looked_up()
    {
        var reader = Substitute.For<IWorkflowRunStatusReader>();

        (await reader.ReadRunAsync(ApplicationTestFactory.CreateTask(), CancellationToken.None)).Should().Be(WorkflowRunStatus.Unknown);

        await reader.DidNotReceive().ReadAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
