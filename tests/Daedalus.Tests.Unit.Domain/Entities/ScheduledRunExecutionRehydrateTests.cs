using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Domain.Entities;

/// <summary>Unit tests for <see cref="ScheduledRunExecution.Rehydrate"/>.</summary>
public sealed class ScheduledRunExecutionRehydrateTests
{
    private static readonly DateTime _at = new(2026, 11, 5, 7, 0, 0, DateTimeKind.Utc);

    /// <summary>
    ///     A stored execution loads as it is, even one <see cref="ScheduledRunExecution.Create"/> would refuse today: no
    ///     schedule id, a non-UTC occurrence, blank identity strings and no roles; with every other value kept, the
    ///     strings not trimmed, and a failed row with its outputs, attempts and error. Red: validate or trim in
    ///     <c>Rehydrate</c>, or drop any one assignment.
    /// </summary>
    [Fact]
    public void Rehydrate_keeps_every_value_including_ones_Create_rejects()
    {
        var local = new DateTime(2026, 11, 5, 8, 0, 0, DateTimeKind.Unspecified);
        ScheduledRunExecution.Create(Guid.Empty, local, " ", " ", " ", [], _at)
            .IsFailure.Should().BeTrue("no schedule id, a non-UTC occurrence and blank ids are what Create refuses");
        var id = Guid.NewGuid();

        var execution = ScheduledRunExecution.Rehydrate(
            id, Guid.Empty, local, RunStep.Failed, RunStep.Writer, " findings ", " digest ", " ", " ", " ", [],
            attempts: 3, lastError: " boom ", createdAt: _at, updatedAt: _at.AddMinutes(5));

        execution.Id.Should().Be(id);
        execution.ScheduleId.Should().Be(Guid.Empty);
        execution.OccurrenceAt.Should().Be(local);
        execution.Step.Should().Be(RunStep.Failed);
        execution.FailedAtStep.Should().Be(RunStep.Writer);
        execution.Findings.Should().Be(" findings ");
        execution.Digest.Should().Be(" digest ");
        execution.ChannelId.Should().Be(" ");
        execution.ConversationId.Should().Be(" ");
        execution.PrincipalId.Should().Be(" ");
        execution.Roles.Should().BeEmpty();
        execution.Attempts.Should().Be(3);
        execution.LastError.Should().Be(" boom ");
        execution.CreatedAt.Should().Be(_at);
        execution.UpdatedAt.Should().Be(_at.AddMinutes(5));
    }

    /// <summary>
    ///     The roles are the list given, in order, and the optional values stay null. Red: sort, copy into a set, or
    ///     default any null.
    /// </summary>
    [Fact]
    public void Rehydrate_keeps_the_roles_in_order_and_the_nulls()
    {
        var execution = ScheduledRunExecution.Rehydrate(
            Guid.NewGuid(), Guid.NewGuid(), _at, RunStep.Scout, null, null, null, "telegram", "1", "p",
            ["writer", "reader"], attempts: 0, lastError: null, createdAt: _at, updatedAt: _at);

        execution.Roles.Should().Equal("writer", "reader");
        execution.Step.Should().Be(RunStep.Scout);
        execution.FailedAtStep.Should().BeNull();
        execution.Findings.Should().BeNull();
        execution.Digest.Should().BeNull();
        execution.LastError.Should().BeNull();
    }
}
