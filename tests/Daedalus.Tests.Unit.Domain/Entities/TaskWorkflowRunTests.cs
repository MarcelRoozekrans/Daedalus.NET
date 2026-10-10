using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Domain.Entities;

/// <summary>
///     Phase 2.8: a task records the manufacture run started for it. Its status is derived from that run when it is
///     read, so attaching a run never touches the stored status.
/// </summary>
public sealed class TaskWorkflowRunTests
{
    /// <summary>
    ///     A task created after phase 2.8 carries none of the loop's settings, so the Web shows none for it.
    ///     Red: keep a default <c>MaxIterations = 10</c> in <c>Create</c>; the assertion fails.
    /// </summary>
    [Fact]
    public void A_new_task_carries_no_loop_settings()
    {
        var task = DomainTask.Create(Guid.NewGuid(), Guid.NewGuid(), "TASK-1", "T", "D", Priority.Medium, "Backend", 1, Complexity.Medium, "P").Value;

        task.MaxIterations.Should().Be(0);
        task.CompletionPromise.Should().BeEmpty();
    }

    /// <summary>Red: drop the assignment in <c>AttachRun</c>; <c>WorkflowRunId</c> stays null.</summary>
    [Fact]
    public void Attaching_a_run_records_its_id()
    {
        var task = DomainTestFactory.CreateTask();
        var runId = Guid.NewGuid();

        task.AttachRun(runId).IsSuccess.Should().BeTrue();

        task.WorkflowRunId.Should().Be(runId);
    }

    /// <summary>Red: write <c>WorkflowRunId ??= runId</c>; the first id is kept and the assertion fails.</summary>
    [Fact]
    public void Starting_again_replaces_the_run_id()
    {
        var task = DomainTestFactory.CreateTask();
        var second = Guid.NewGuid();
        task.AttachRun(Guid.NewGuid());

        task.AttachRun(second);

        task.WorkflowRunId.Should().Be(second);
    }

    /// <summary>Red: drop the <c>Guid.Empty</c> check; the call succeeds and records the empty id.</summary>
    [Fact]
    public void An_empty_run_id_is_refused_and_nothing_changes()
    {
        var task = DomainTestFactory.CreateTask();

        var attached = task.AttachRun(Guid.Empty);

        attached.IsFailure.Should().BeTrue();
        task.WorkflowRunId.Should().BeNull();
    }

    /// <summary>Red: set <c>Status = TaskStatus.InProgress</c> in <c>AttachRun</c>; the stored status changes.</summary>
    [Fact]
    public void Attaching_a_run_leaves_the_stored_status_alone()
    {
        var task = DomainTestFactory.CreateTask();

        task.AttachRun(Guid.NewGuid());

        task.Status.Should().Be(DomainTaskStatus.Pending);
    }

    /// <summary>
    ///     The derived values must not reuse a stored integer, or an old row would read as the new status.
    ///     Red: renumber <c>AwaitingApproval</c> to 4, or <c>Cancelled</c> to 3.
    /// </summary>
    [Fact]
    public void The_derived_statuses_do_not_reuse_a_stored_value()
    {
        ((int)DomainTaskStatus.AwaitingApproval).Should().Be(5);
        ((int)DomainTaskStatus.Cancelled).Should().Be(6);
    }
}
