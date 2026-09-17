using AwesomeAssertions;
using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Domain;

public class ScheduledRunTests
{
    private static readonly DateTime Now = new(2026, 9, 16, 7, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Create_returns_an_enabled_config_row_with_its_first_occurrence()
    {
        var result = ScheduledRun.Create(
            "daily-digest", "0 7 * * *", "RepoDigestSaga", "telegram", "123456",
            "schedule:daily-digest", ["reader"], ScheduleOrigin.Config, Now);

        result.IsSuccess.Should().BeTrue();
        result.Value.Enabled.Should().BeTrue();
        result.Value.NextRunAt.Should().Be(Now);
        result.Value.MissedOccurrences.Should().Be(0);
        result.Value.Origin.Should().Be(ScheduleOrigin.Config);
    }

    [Theory]
    [InlineData("", "0 7 * * *", "name")]
    [InlineData("daily-digest", "", "cron")]
    public void Create_rejects_blank_required_fields(string name, string cron, string because)
    {
        var result = ScheduledRun.Create(
            name, cron, "RepoDigestSaga", "telegram", "123456",
            "schedule:x", ["reader"], ScheduleOrigin.Config, Now);

        result.IsFailure.Should().BeTrue(because);
    }

    [Fact]
    public void Create_rejects_an_empty_conversation_id()
    {
        // unlike ChannelConversation, a schedule with no delivery target is useless: there is no
        // live turn to fall back on, so the digest would run and have nowhere to go
        var result = ScheduledRun.Create(
            "daily-digest", "0 7 * * *", "RepoDigestSaga", "telegram", "",
            "schedule:x", ["reader"], ScheduleOrigin.Config, Now);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void AdvanceTo_moves_the_next_occurrence_and_records_the_run()
    {
        var run = ScheduledRun.Create(
            "daily-digest", "0 7 * * *", "RepoDigestSaga", "telegram", "123456",
            "schedule:x", ["reader"], ScheduleOrigin.Config, Now).Value;

        run.AdvanceTo(Now.AddDays(1), Now, missed: 0);

        run.NextRunAt.Should().Be(Now.AddDays(1));
        run.LastRunAt.Should().Be(Now);
    }

    [Fact]
    public void AdvanceTo_accumulates_missed_occurrences()
    {
        var run = ScheduledRun.Create(
            "daily-digest", "0 7 * * *", "RepoDigestSaga", "telegram", "123456",
            "schedule:x", ["reader"], ScheduleOrigin.Config, Now).Value;

        run.AdvanceTo(Now.AddDays(1), Now, missed: 3);
        run.AdvanceTo(Now.AddDays(2), Now.AddDays(1), missed: 2);

        run.MissedOccurrences.Should().Be(5, "the count is cumulative, for observability over time");
    }
}
