using Daedalus.Application.DTOs.Scheduling;
using Daedalus.Web.Components;
using Radzen;

namespace Daedalus.Tests.Unit.Web;

/// <summary>
///     Pins <see cref="VerdictBadge"/>'s severity mapping and, just as importantly, its reuse: anything else on
///     the schedules page that needs a verdict's colour — like the mobile card's error line — must derive it
///     from <see cref="VerdictBadge.BadgeStyleFor"/>/<see cref="VerdictBadge.ColorToken"/> rather than switching
///     over <see cref="RunVerdict"/> a second time.
/// </summary>
/// <remarks>
///     The regression this guards against: the mobile card's error line originally hardcoded
///     <c>--rz-danger</c> for every non-null error line, which is wrong for <see cref="RunVerdict.Stranded"/>
///     and <see cref="RunVerdict.DeliveryUnknown"/> — both Warning severity, same as the badge already showing
///     above it. A single shared mapping makes that particular drift impossible to reintroduce.
/// </remarks>
public sealed class VerdictBadgeTests
{
    [Theory]
    [InlineData(RunVerdict.Overdue)]
    [InlineData(RunVerdict.Stranded)]
    [InlineData(RunVerdict.DeliveryUnknown)]
    public void Warning_severity_verdicts_map_to_the_warning_token_not_danger(RunVerdict verdict)
    {
        VerdictBadge.BadgeStyleFor(verdict).Should().Be(BadgeStyle.Warning);
        VerdictBadge.ColorToken(verdict).Should().Be("var(--rz-warning)");
    }

    [Theory]
    [InlineData(RunVerdict.Failed)]
    [InlineData(RunVerdict.Undelivered)]
    public void Danger_severity_verdicts_map_to_the_danger_token(RunVerdict verdict)
    {
        VerdictBadge.BadgeStyleFor(verdict).Should().Be(BadgeStyle.Danger);
        VerdictBadge.ColorToken(verdict).Should().Be("var(--rz-danger)");
    }

    [Fact]
    public void ColorToken_never_disagrees_with_BadgeStyleFor_for_any_verdict()
    {
        foreach (var verdict in Enum.GetValues<RunVerdict>())
        {
            var style = VerdictBadge.BadgeStyleFor(verdict);
            var token = VerdictBadge.ColorToken(verdict);

            var expectedToken = style switch
            {
                BadgeStyle.Success => "var(--rz-success)",
                BadgeStyle.Info => "var(--rz-info)",
                BadgeStyle.Warning => "var(--rz-warning)",
                BadgeStyle.Danger => "var(--rz-danger)",
                _ => "var(--rz-text-color)",
            };

            token.Should().Be(expectedToken, $"{verdict} is {style} severity, so its colour token must match");
        }
    }

    [Fact]
    public void An_unmapped_verdict_reads_as_muted_rather_than_throwing()
    {
        // RunVerdict.Unknown = 0 is a sentinel never returned by the service. A cast out-of-range value stands
        // in for a future member this page has not been taught yet. Neither may throw: a switch with no
        // discard arm blanks the page, which is the one failure mode this page exists to avoid.
        var unmapped = (RunVerdict)999;

        VerdictBadge.BadgeStyleFor(RunVerdict.Unknown).Should().Be(BadgeStyle.Light);
        VerdictBadge.BadgeStyleFor(unmapped).Should().Be(BadgeStyle.Light);
        VerdictBadge.ColorToken(unmapped).Should().Be("var(--rz-text-color)");
    }
}
