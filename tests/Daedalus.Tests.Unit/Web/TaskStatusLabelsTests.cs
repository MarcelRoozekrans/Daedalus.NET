using Daedalus.Web.Services;
using Radzen;

namespace Daedalus.Tests.Unit.Web;

/// <summary>Phase 2.8: the three Web status switches share one table, which knows the derived values.</summary>
public sealed class TaskStatusLabelsTests
{
    /// <summary>Red per row: drop that case; it reads "Unknown".</summary>
    [Theory]
    [InlineData(0, "Pending")]
    [InlineData(1, "In Progress")]
    [InlineData(2, "Completed")]
    [InlineData(3, "Failed")]
    [InlineData(4, "Abandoned")]
    [InlineData(5, "Awaiting Approval")]
    [InlineData(6, "Cancelled")]
    public void Each_status_has_its_label(int status, string label) => TaskStatusLabels.Name(status).Should().Be(label);

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

    /// <summary>Red: give AwaitingApproval the Info style of InProgress; a person at the board cannot tell them apart.</summary>
    [Fact]
    public void Awaiting_approval_looks_different_from_in_progress() =>
        TaskStatusLabels.Style(5).Should().NotBe(TaskStatusLabels.Style(1));

    /// <summary>Red per row: change a status's style in <c>Style</c>.</summary>
    [Theory]
    [InlineData(0, BadgeStyle.Warning)]
    [InlineData(1, BadgeStyle.Info)]
    [InlineData(2, BadgeStyle.Success)]
    [InlineData(3, BadgeStyle.Danger)]
    [InlineData(4, BadgeStyle.Light)]
    [InlineData(5, BadgeStyle.Primary)]
    [InlineData(6, BadgeStyle.Light)]
    [InlineData(99, BadgeStyle.Light)]
    public void Each_status_has_its_style(int status, BadgeStyle style) => TaskStatusLabels.Style(status).Should().Be(style);

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

    /// <summary>
    ///     The newest of the task's own timestamps.
    ///     Red: never take CompletedAt; the completion is ignored. Red: never take LearningsUpdatedAt; the update is lost.
    ///     Red: start from DateTime.MinValue instead of CreatedAt; a task with neither reads year 1.
    /// </summary>
    [Fact]
    public void A_task_last_changed_at_its_newest_timestamp()
    {
        var created = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var task = Task(0, null) with { CreatedAt = created, CompletedAt = created.AddDays(1), LearningsUpdatedAt = null };
        TaskStatusLabels.LastChanged(task).Should().Be(created.AddDays(1));
        TaskStatusLabels.LastChanged(task with { LearningsUpdatedAt = created.AddDays(2) }).Should().Be(created.AddDays(2));
        TaskStatusLabels.LastChanged(task with { CompletedAt = null }).Should().Be(created);
    }

    private static TaskDto Task(int maxIterations, Uri? pullRequestUrl) =>
        new(Guid.NewGuid(), "T-1", Guid.NewGuid(), "t", "d", 1, "p", 0, [], [], 1, "prompt", "promise", maxIterations, 0,
            null, null, 0, DateTime.UtcNow, null, null, null, [], null, pullRequestUrl);

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
