using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Domain.Entities;

/// <summary>Unit tests for <c>Rehydrate</c> on the agent session.</summary>
public sealed class AgentSessionRehydrateTests
{
    private static readonly DateTime _at = new(2026, 11, 5, 9, 15, 30, DateTimeKind.Utc);

    /// <summary>
    ///     A stored session loads as it is, even one <see cref="AgentSession.Create"/> would refuse today: an empty id, an
    ///     empty agent id, a blank owner, a last activity before the creation and negative counters.
    ///     Red: validate in <c>Rehydrate</c>, or drop any one assignment.
    /// </summary>
    [Fact]
    public void Rehydrate_keeps_every_value_including_ones_Create_rejects()
    {
        AgentSession.Create(Guid.Empty, Guid.Empty, " ", _at).IsFailure.Should().BeTrue("the values below are ones Create refuses");

        var session = AgentSession.Rehydrate(
            Guid.Empty, Guid.Empty, " ", AgentSessionState.Running, _at, _at.AddHours(-1), -1, -2, -3);

        session.Id.Should().Be(Guid.Empty);
        session.AgentId.Should().Be(Guid.Empty);
        session.OwnerId.Should().Be(" ");
        session.State.Should().Be(AgentSessionState.Running);
        session.CreatedAt.Should().Be(_at);
        session.LastActivityAt.Should().Be(_at.AddHours(-1));
        session.TurnCount.Should().Be(-1);
        session.TotalInputTokens.Should().Be(-2);
        session.TotalOutputTokens.Should().Be(-3);
    }
}
