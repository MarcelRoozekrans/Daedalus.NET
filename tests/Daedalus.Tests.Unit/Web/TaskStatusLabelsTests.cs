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
    ///     A task can be started unless its run is live or it already completed.
    ///     Red per row: change <c>IsStartable</c>'s pattern.
    /// </summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(6, true)]
    public void Only_a_task_without_a_live_or_completed_run_is_startable(int status, bool startable) =>
        TaskStatusLabels.IsStartable(status).Should().Be(startable);

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

    private static TaskDto Task(int maxIterations, Uri? pullRequestUrl) =>
        new(Guid.NewGuid(), "T-1", Guid.NewGuid(), "t", "d", 1, "p", 0, [], [], 1, "prompt", "promise", maxIterations, 0,
            null, null, 0, DateTime.UtcNow, null, null, null, [], null, pullRequestUrl);
}
