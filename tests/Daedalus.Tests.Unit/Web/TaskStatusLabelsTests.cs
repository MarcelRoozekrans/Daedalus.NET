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
}
