using Daedalus.Api.Services;
using Daedalus.Application.DTOs.WorkflowRuns;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>A failed run's reason, derived on read from its steps, its last error and the lens that last rejected it.</summary>
public sealed class RunFailureReasonTests
{
    private static long _seq;

    private static WorkflowRunStepView Step(string kind, string from, string to, string status = "Running", string? outcome = null) =>
        new(_seq++, kind, from, to, status, outcome, null, null, null, null, DateTimeOffset.UnixEpoch, null);

    private static WorkflowRunStepView Rejected() => Step("Completed", "review", "implement", outcome: "rejected");

    /// <summary>
    ///     Three rejected review steps and a CapExceeded step name the count and the rejecting lens.
    ///     Red: count every step from review, not only the rejected ones. Red: add one to the count.
    ///     Red: drop the lens from the sentence.
    /// </summary>
    [Fact]
    public void A_capped_review_names_the_count_and_the_last_rejecting_lens()
    {
        WorkflowRunStepView[] steps =
        [
            Step("Completed", "implement", "review", outcome: "done"),
            Rejected(),
            Step("Completed", "implement", "review", outcome: "done"),
            Rejected(),
            Step("Completed", "implement", "review", outcome: "done"),
            Rejected(),
            Step("CapExceeded", "review", "failed", "Failed"),
        ];

        RunFailureReason.Derive("Failed", steps, "visit cap", "mechanism").Should().Be(
            "Review rejected the change 3 times, the review limit. Last rejecting lens: mechanism.");
    }

    /// <summary>
    ///     When the lens cannot be read the second sentence is left out.
    ///     Red: write the lens sentence whether or not a lens was read. Red: add one to the count.
    /// </summary>
    [Fact]
    public void A_capped_review_without_a_readable_lens_leaves_the_second_sentence_out()
    {
        WorkflowRunStepView[] steps = [Rejected(), Rejected(), Step("CapExceeded", "review", "failed", "Failed")];

        RunFailureReason.Derive("Failed", steps, null, null).Should().Be("Review rejected the change 2 times, the review limit.");
    }

    /// <summary>
    ///     An implement step that completed as blocked says so, even with a last error present.
    ///     Red: check lastError before the blocked outcome.
    /// </summary>
    [Fact]
    public void A_blocked_implement_says_so()
    {
        WorkflowRunStepView[] steps = [Step("Completed", "implement", "failed", "Failed", "blocked")];

        RunFailureReason.Derive("Failed", steps, "some error", null).Should().Be("Implement reported it is blocked.");
    }

    /// <summary>
    ///     A blocked outcome on a node other than implement is not a blocked implement.
    ///     Red: drop the implement node condition from the blocked check.
    /// </summary>
    [Fact]
    public void A_blocked_outcome_on_another_node_is_not_a_blocked_implement()
    {
        WorkflowRunStepView[] steps = [Step("Completed", "review", "failed", "Failed", "blocked")];

        RunFailureReason.Derive("Failed", steps, "some error", null).Should().Be("some error");
    }

    /// <summary>
    ///     A host error is the reason as it stands.
    ///     Red: return null for lastError.
    /// </summary>
    [Fact]
    public void A_host_error_is_the_reason()
    {
        WorkflowRunStepView[] steps = [Step("Failed", "implement", "failed", "Failed")];

        RunFailureReason.Derive("Failed", steps, "the sandbox went away", null).Should().Be("the sandbox went away");
    }

    /// <summary>
    ///     With nothing else to go on the reason names the last step's node.
    ///     Red: use the first step's toNode.
    /// </summary>
    [Fact]
    public void An_unknown_failure_path_falls_back_to_the_terminal_node()
    {
        WorkflowRunStepView[] steps = [Step("Entered", "", "implement"), Step("Failed", "implement", "publish", "Failed")];

        RunFailureReason.Derive("Failed", steps, null, null).Should().Be("The run ended at publish.");
    }

    /// <summary>
    ///     A blank last error is not a reason. Red: test lastError with is not null instead of IsNullOrWhiteSpace.
    /// </summary>
    [Fact]
    public void A_blank_host_error_falls_through()
    {
        WorkflowRunStepView[] steps = [Step("Failed", "implement", "publish", "Failed")];

        RunFailureReason.Derive("Failed", steps, "  ", null).Should().Be("The run ended at publish.");
    }

    /// <summary>
    ///     Only a Failed run has a reason, whatever its steps say.
    ///     Red: drop the status guard.
    /// </summary>
    [Theory]
    [InlineData("Succeeded")]
    [InlineData("Awaiting")]
    [InlineData("Cancelled")]
    [InlineData("Running")]
    public void A_run_that_did_not_fail_has_no_reason(string status)
    {
        WorkflowRunStepView[] steps = [Step("CapExceeded", "review", "failed", "Failed")];

        RunFailureReason.Derive(status, steps, "an old error", "mechanism").Should().BeNull();
    }
}
