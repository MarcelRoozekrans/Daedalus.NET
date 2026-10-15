using Daedalus.Web.Services;
using Radzen;

namespace Daedalus.Tests.Unit.Web;

/// <summary>The UI contract's status mapping: text plus severity, never colour alone.</summary>
public sealed class RunStatusLabelsTests
{
    /// <summary>
    ///     Each run status maps to the contract's text and severity.
    ///     Red: map Awaiting to Info. Red: render Awaiting with any signal as "Awaiting approval".
    /// </summary>
    [Theory]
    [InlineData("Running", null, "Running", BadgeStyle.Info)]
    [InlineData("Awaiting", "human_approval", "Awaiting approval", BadgeStyle.Warning)]
    [InlineData("Awaiting", "other_signal", "Waiting: other_signal", BadgeStyle.Warning)]
    [InlineData("Succeeded", null, "Succeeded", BadgeStyle.Success)]
    [InlineData("Failed", null, "Failed", BadgeStyle.Danger)]
    [InlineData("Cancelled", null, "Cancelled", BadgeStyle.Light)]
    [InlineData("Unheard", null, "Unheard", BadgeStyle.Light)]
    public void Run_status(string status, string? signal, string text, BadgeStyle style)
    {
        RunStatusLabels.Text(status, signal).Should().Be(text);
        RunStatusLabels.Style(status, signal).Should().Be(style);
    }

    /// <summary>
    ///     Each step kind or outcome maps to the contract's severity and text.
    ///     Red: map rejected to Light. Red: show the kind instead of the outcome for a completion.
    ///     Red: restore outcome ?? kind in StepText: the CapExceeded and Failed rows with outcome changed fail.
    ///     Red: drop the none check in StepText: the Completed rows with outcome none or None show "none".
    /// </summary>
    [Theory]
    [InlineData("Completed", "approved", "approved", BadgeStyle.Success)]
    [InlineData("Completed", "published", "published", BadgeStyle.Success)]
    [InlineData("Completed", "rejected", "rejected", BadgeStyle.Danger)]
    [InlineData("Failed", null, "Failed", BadgeStyle.Danger)]
    [InlineData("CapExceeded", null, "CapExceeded", BadgeStyle.Danger)]
    [InlineData("Resumed", null, "Resumed", BadgeStyle.Info)]
    [InlineData("Retried", null, "Retried", BadgeStyle.Info)]
    [InlineData("Completed", "done", "done", BadgeStyle.Light)]
    [InlineData("Entered", null, "Entered", BadgeStyle.Light)]
    [InlineData("Cancelled", null, "Cancelled", BadgeStyle.Light)]
    [InlineData("CapExceeded", "changed", "CapExceeded", BadgeStyle.Danger)]
    [InlineData("Failed", "changed", "Failed", BadgeStyle.Danger)]
    [InlineData("Cancelled", "approved", "Cancelled", BadgeStyle.Light)]
    [InlineData("Retried", "approved", "Retried", BadgeStyle.Info)]
    [InlineData("Completed", "none", "Completed", BadgeStyle.Light)]
    [InlineData("Completed", "None", "Completed", BadgeStyle.Light)]
    public void Step(string kind, string? outcome, string text, BadgeStyle style)
    {
        RunStatusLabels.StepText(kind, outcome).Should().Be(text);
        RunStatusLabels.StepStyle(kind, outcome).Should().Be(style);
    }

    /// <summary>
    ///     A step shows a badge unless its outcome is <c>none</c> and its kind is not one of the five that always show.
    ///     Red: <c>ShowsStepBadge</c> returns true; the Completed rows with outcome none or None are shown.
    ///     Red: <c>ShowsStepBadge</c> ignores the kind; the Failed and Retried rows with outcome none are hidden.
    ///     Red: <c>ShowsStepBadge</c> hides a null outcome too; the Entered row is hidden.
    ///     Red: <c>ShowsStepBadge</c> returns <c>IsSignificantKind(kind)</c> only; the Completed approved row is hidden.
    /// </summary>
    [Theory]
    [InlineData("Completed", "none", false)]
    [InlineData("Completed", "None", false)]
    [InlineData("Completed", "approved", true)]
    [InlineData("Entered", null, true)]
    [InlineData("Failed", "none", true)]
    [InlineData("CapExceeded", "none", true)]
    [InlineData("Cancelled", "none", true)]
    [InlineData("Resumed", "none", true)]
    [InlineData("Retried", "none", true)]
    public void Step_badge_shown(string kind, string? outcome, bool shown) =>
        RunStatusLabels.ShowsStepBadge(kind, outcome).Should().Be(shown);

    /// <summary>
    ///     A review verdict maps to the contract's severity: Unreadable is a Warning and never reads as Approved, and a
    ///     verdict the contract does not name is muted.
    ///     Red: map Unreadable to Success. Red: map Rejected to Warning. Red: map the default to Success.
    ///     Red: map Approved to Info; the Approved row fails.
    /// </summary>
    [Theory]
    [InlineData("Approved", BadgeStyle.Success)]
    [InlineData("Rejected", BadgeStyle.Danger)]
    [InlineData("Unreadable", BadgeStyle.Warning)]
    [InlineData("Unheard", BadgeStyle.Light)]
    public void Review_verdict(string verdict, BadgeStyle style) =>
        RunStatusLabels.VerdictStyle(verdict).Should().Be(style);

    /// <summary>
    ///     A rejection and an unreadable visit wait for implement's answer; an approval, and a verdict the contract does not
    ///     name, do not.
    ///     Red: <c>VerdictAwaitsAnswer</c> checks Rejected only; the Unreadable row fails.
    ///     Red: <c>VerdictAwaitsAnswer</c> returns true; the Approved and unknown rows fail.
    ///     Red: <c>VerdictAwaitsAnswer</c> checks Unreadable only; the Rejected row fails.
    /// </summary>
    [Theory]
    [InlineData(RunStatusLabels.VerdictRejected, true)]
    [InlineData(RunStatusLabels.VerdictUnreadable, true)]
    [InlineData(RunStatusLabels.VerdictApproved, false)]
    [InlineData("Unheard", false)]
    public void Verdict_awaits_answer(string verdict, bool awaits) =>
        RunStatusLabels.VerdictAwaitsAnswer(verdict).Should().Be(awaits);

    /// <summary>
    ///     The verdict constants are the API's verdict strings, which the views carry as text.
    ///     Red: change <c>VerdictUnreadable</c> to "Unknown"; the third value differs.
    /// </summary>
    [Fact]
    public void Verdict_constants_are_the_apis_strings() =>
        new[] { RunStatusLabels.VerdictApproved, RunStatusLabels.VerdictRejected, RunStatusLabels.VerdictUnreadable }
            .Should().Equal("Approved", "Rejected", "Unreadable");

    /// <summary>
    ///     A starter is named by display name, else "unknown".
    ///     Red: <c>RunStarter.Name</c> returns <c>displayName ?? ""</c>; the null row fails.
    ///     Red: <c>RunStarter.Name</c> returns <see cref="RunStarter.Unknown"/> always; the named row fails.
    /// </summary>
    [Theory]
    [InlineData("Dev Eloper", "Dev Eloper")]
    [InlineData(null, "unknown")]
    public void Starter_name(string? displayName, string shown) =>
        RunStarter.Name(displayName).Should().Be(shown);
}
