using Daedalus.Web.Services;
using Radzen;

namespace Daedalus.Tests.Unit.Web;

/// <summary>Phase 2.8: what a task offers comes from its derived status; phase 2.13: its badge is its run's, or its own.</summary>
public sealed class TaskStatusLabelsTests
{
    /// <summary>
    ///     A task with a run shows its run's badge, mapped by RunStatusLabels exactly as the Runs page maps it. A task with
    ///     no run shows its own status, stored or derived, muted: Pending for a new task, and the retired loop's frozen
    ///     history, such as Completed or Failed, as itself. A stored Failed is muted, not the Danger of a failed run.
    ///     Red: NoRunText returns "Pending" for every task with no run; the Completed, Failed and Abandoned rows fail.
    ///     Red: BadgeStyleOf maps a task with no run through RunStatusLabels; the stored Failed row reads Danger.
    ///     Red: BadgeText names a task with a run by its own status; the three run rows fail.
    ///     Red: RunStatusLabels' Awaiting style Primary; the Awaiting row fails.
    /// </summary>
    [Theory]
    [InlineData(0, null, null, "Pending", BadgeStyle.Light)]
    [InlineData(2, null, null, "Completed", BadgeStyle.Light)]
    [InlineData(3, null, null, "Failed", BadgeStyle.Light)]
    [InlineData(4, null, null, "Abandoned", BadgeStyle.Light)]
    [InlineData(5, "Awaiting", "human_approval", "Awaiting approval", BadgeStyle.Warning)]
    [InlineData(1, "Running", null, "Running", BadgeStyle.Info)]
    [InlineData(2, "Succeeded", null, "Succeeded", BadgeStyle.Success)]
    public void A_tasks_badge_is_its_runs_or_its_own_muted(int status, string? runStatus, string? signal, string text, BadgeStyle style)
    {
        var task = Task(0, null) with { Status = status, RunStatus = runStatus, AwaitingSignal = signal };
        TaskStatusLabels.BadgeText(task).Should().Be(text);
        TaskStatusLabels.BadgeStyleOf(task).Should().Be(style);
    }

    /// <summary>Red: return the name for a task with a run too; the run case is not null.</summary>
    [Fact]
    public void Only_a_task_without_a_run_has_its_own_badge_text() =>
        TaskStatusLabels.NoRunText(Task(0, null) with { RunStatus = "Running", Status = 1 }).Should().BeNull();

    /// <summary>
    ///     A task with no run can be started unless it already completed or is In Progress.
    ///     Red per row: change <c>IsEditable</c>'s pattern, which <c>IsStartable</c> builds on.
    /// </summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(6, true)]
    public void A_task_without_a_run_is_startable_unless_it_completed(int status, bool startable) =>
        TaskStatusLabels.IsStartable(WithRun(status, workflowRunId: null)).Should().Be(startable);

    /// <summary>
    ///     A task stored In Progress with no run is an orphaned claim of the retired loop, which the server starts.
    ///     Red: drop the orphan clause from <c>IsStartable</c>; the orphan is not startable.
    /// </summary>
    [Fact]
    public void An_orphaned_in_progress_claim_without_a_run_is_startable() =>
        TaskStatusLabels.IsStartable(WithRun(1, workflowRunId: null)).Should().BeTrue();

    /// <summary>
    ///     In Progress with a run is a live run, and Awaiting Approval always has one. Neither offers Manufacture.
    ///     Red: drop the <c>WorkflowRunId is null</c> test from the orphan clause; the live run is startable.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void A_task_whose_run_is_live_is_not_startable(int status) =>
        TaskStatusLabels.IsStartable(WithRun(status, workflowRunId: Guid.NewGuid())).Should().BeFalse();

    /// <summary>
    ///     Ruling I2: a task can be edited unless its run is live or it completed; a failed or cancelled run leaves it
    ///     editable. The server's <c>TaskStatusDerivation.IsEditable</c> uses the same set.
    ///     Red per row: change <c>IsEditable</c>'s pattern, for example back to the dialog's old <c>0 or 3</c>.
    /// </summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(6, true)]
    public void Only_a_task_without_a_live_or_completed_run_is_editable(int status, bool editable) =>
        TaskStatusLabels.IsEditable(status).Should().Be(editable);

    /// <summary>Red: change <c>MaxIterations &gt; 0</c> to <c>&gt;= 0</c>; zero reads true.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(10, true)]
    public void Only_a_task_with_iterations_has_loop_history(int maxIterations, bool expected) =>
        TaskStatusLabels.HasLoopHistory(Task(maxIterations, null)).Should().Be(expected);

    /// <summary>Red: return null or a fixed string from <c>PullRequestHref</c>; the link is missing or wrong.</summary>
    [Fact]
    public void A_task_with_a_pull_request_exposes_its_link() =>
        TaskStatusLabels.PullRequestHref(Task(0, new Uri("https://github.com/o/r/pull/7"))).Should().Be("https://github.com/o/r/pull/7");

    /// <summary>Red: return a placeholder when there is no pull request; a dead link is rendered.</summary>
    [Fact]
    public void A_task_without_a_pull_request_has_no_link() =>
        TaskStatusLabels.PullRequestHref(Task(0, null)).Should().BeNull();

    /// <summary>Red: return the whole URL; it is not "#7".</summary>
    [Fact]
    public void A_pull_request_reads_as_its_number() =>
        TaskStatusLabels.PullRequestText(Task(0, new Uri("https://github.com/o/r/pull/7"))).Should().Be("#7");

    /// <summary>Red: take the last segment whatever it is; a URL without a number reads "#files".</summary>
    [Fact]
    public void A_pull_request_url_without_a_number_reads_as_pull_request() =>
        TaskStatusLabels.PullRequestText(Task(0, new Uri("https://github.com/o/r/pull/7/files"))).Should().Be("Pull request");

    /// <summary>Red: return "—" instead of null; the grid renders a dead link.</summary>
    [Fact]
    public void A_task_without_a_pull_request_has_no_pull_request_text() =>
        TaskStatusLabels.PullRequestText(Task(0, null)).Should().BeNull();

    private static TaskDto Task(int maxIterations, Uri? pullRequestUrl) =>
        new(Guid.NewGuid(), "T-1", Guid.NewGuid(), "t", "d", 1, "p", 0, [], [], 1, "prompt", "promise", maxIterations, 0,
            null, null, 0, DateTime.UtcNow, null, null, null, [], null, pullRequestUrl, null, null);

    /// <summary>
    ///     The Web's edit rule and the server's are two copies of one set; a status the dialog offers Save for must be
    ///     one the handler accepts. Red per row: change either side's pattern for that status.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void The_web_and_the_server_agree_on_which_statuses_are_editable(int status) =>
        TaskStatusLabels.IsEditable(status).Should().Be(
            Daedalus.Application.Services.TaskStatusDerivation.IsEditable((Daedalus.Domain.Entities.TaskStatus)status));

    private static TaskDto WithRun(int status, Guid? workflowRunId) =>
        Task(0, pullRequestUrl: null) with { Status = status, WorkflowRunId = workflowRunId };
}
