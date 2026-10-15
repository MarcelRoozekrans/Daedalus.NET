using Daedalus.Application.Abstractions;
using Daedalus.Application.Mappers;
using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Application.Mappers;

/// <summary>
///     A task carries its run's own status and signal, so the Web shows a task's run exactly as it shows the run. A task
///     with no run carries none.
/// </summary>
public sealed class TaskDtoMapperRunStatusTests
{
    private static DomainTask NewTask() =>
        DomainTask.Create(Guid.NewGuid(), Guid.NewGuid(), "T-1", "Title", "Description", Priority.Medium, "Backend", 1,
            Complexity.Low, "Prompt").Value;

    /// <summary>
    ///     Red: map RunStatus from the derived task status instead of the run's state; it reads "AwaitingApproval".
    ///     Red: drop AwaitingSignal from the mapping; the signal is null.
    /// </summary>
    [Fact]
    public void A_task_with_a_run_carries_the_runs_status_and_signal()
    {
        var task = NewTask();
        task.AttachRun(Guid.NewGuid()).IsSuccess.Should().BeTrue();
        var run = new WorkflowRunStatus(WorkflowRunState.Awaiting, null, "human_approval");

        var full = TaskDtoMapper.ToDto(task, run);
        var light = TaskDtoMapper.ToDtoWithoutExecutions(task, run);

        full.RunStatus.Should().Be("Awaiting");
        full.AwaitingSignal.Should().Be("human_approval");
        light.RunStatus.Should().Be("Awaiting");
        light.AwaitingSignal.Should().Be("human_approval");
    }

    /// <summary>
    ///     A task with no run carries no run status, even when a status read says otherwise, so its badge shows its own
    ///     stored or derived status, muted.
    ///     Red: map RunStatus whatever the task holds; it reads "Unknown".
    /// </summary>
    [Fact]
    public void A_task_without_a_run_carries_no_run_status()
    {
        TaskDtoMapper.ToDto(NewTask(), WorkflowRunStatus.Unknown).RunStatus.Should().BeNull();
        TaskDtoMapper.ToDtoWithoutExecutions(NewTask(), WorkflowRunStatus.Unknown).RunStatus.Should().BeNull();
    }
}
