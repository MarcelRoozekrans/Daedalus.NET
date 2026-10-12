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
    public void Step(string kind, string? outcome, string text, BadgeStyle style)
    {
        RunStatusLabels.StepText(kind, outcome).Should().Be(text);
        RunStatusLabels.StepStyle(kind, outcome).Should().Be(style);
    }
}
