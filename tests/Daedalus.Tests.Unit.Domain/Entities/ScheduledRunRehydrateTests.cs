using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Domain.Entities;

/// <summary>Unit tests for <see cref="ScheduledRun.Rehydrate"/>.</summary>
public sealed class ScheduledRunRehydrateTests
{
    private static readonly DateTime _at = new(2026, 11, 5, 7, 0, 0, DateTimeKind.Utc);

    /// <summary>
    ///     A stored schedule loads as it is, even one <see cref="ScheduledRun.Create"/> would refuse today: a blank name
    ///     and cron and no roles; with every other value kept, the strings not trimmed, and a disabled agent row with a
    ///     last run and missed occurrences. Red: validate or trim in <c>Rehydrate</c>, or drop any one assignment.
    /// </summary>
    [Fact]
    public void Rehydrate_keeps_every_value_including_ones_Create_rejects()
    {
        ScheduledRun.Create(" ", " ", "T", "c", "1", "p", [], ScheduleOrigin.Agent, _at)
            .IsFailure.Should().BeTrue("a blank name, a blank cron and no roles are what Create refuses");
        var id = Guid.NewGuid();

        var run = ScheduledRun.Rehydrate(
            id, " ", " ", " RepoDigest ", " telegram ", " 42 ", " schedule:x ", [], " owner/repo ",
            ScheduleOrigin.Agent, _at, _at.AddDays(-1), enabled: false, missedOccurrences: 3);

        run.Id.Should().Be(id);
        run.Name.Should().Be(" ");
        run.Cron.Should().Be(" ");
        run.Trigger.Should().Be(" RepoDigest ");
        run.ChannelId.Should().Be(" telegram ");
        run.ConversationId.Should().Be(" 42 ");
        run.PrincipalId.Should().Be(" schedule:x ");
        run.Roles.Should().BeEmpty();
        run.Repository.Should().Be(" owner/repo ");
        run.Origin.Should().Be(ScheduleOrigin.Agent);
        run.NextRunAt.Should().Be(_at);
        run.LastRunAt.Should().Be(_at.AddDays(-1));
        run.Enabled.Should().BeFalse();
        run.MissedOccurrences.Should().Be(3);
    }

    /// <summary>
    ///     The roles are the list given, in order, and a null repository and last run stay null. Red: sort, copy into a
    ///     set, or default either null.
    /// </summary>
    [Fact]
    public void Rehydrate_keeps_the_roles_in_order_and_the_nulls()
    {
        var run = ScheduledRun.Rehydrate(
            Guid.NewGuid(), "n", "0 7 * * *", "RepoDigest", "telegram", "1", "p", ["writer", "reader"], null,
            ScheduleOrigin.Config, _at, null, enabled: true, missedOccurrences: 0);

        run.Roles.Should().Equal("writer", "reader");
        run.Repository.Should().BeNull();
        run.LastRunAt.Should().BeNull();
        run.Enabled.Should().BeTrue();
        run.Origin.Should().Be(ScheduleOrigin.Config);
    }
}
