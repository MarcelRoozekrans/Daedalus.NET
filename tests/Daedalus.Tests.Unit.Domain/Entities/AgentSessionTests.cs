using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Domain.Entities;

/// <summary>
///     Unit tests for the AgentSession aggregate root and AgentMessage entity.
/// </summary>
public sealed class AgentSessionTests
{
    [Fact]
    public void Create_sets_defaults()
    {
        var s = AgentSession.Create(Guid.NewGuid(), Guid.NewGuid(), "alice", DateTime.UtcNow).Value;

        s.State.Should().Be(AgentSessionState.Idle);
        s.TurnCount.Should().Be(0);
        s.TotalInputTokens.Should().Be(0);
        s.TotalOutputTokens.Should().Be(0);
        s.OwnerId.Should().Be("alice");
        s.CreatedAt.Should().Be(s.LastActivityAt);
    }

    [Fact]
    public void Create_requires_owner_and_ids()
    {
        AgentSession.Create(Guid.Empty, Guid.NewGuid(), "a", DateTime.UtcNow).IsFailure.Should().BeTrue();
        AgentSession.Create(Guid.NewGuid(), Guid.Empty, "a", DateTime.UtcNow).IsFailure.Should().BeTrue();
        AgentSession.Create(Guid.NewGuid(), Guid.NewGuid(), " ", DateTime.UtcNow).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void RecordTurn_accumulates_and_bumps_activity()
    {
        var t0 = new DateTime(2026, 8, 16, 10, 0, 0, DateTimeKind.Utc);
        var s = AgentSession.Create(Guid.NewGuid(), Guid.NewGuid(), "a", t0).Value;

        s.RecordTurn(100, 20, t0.AddMinutes(1));
        s.RecordTurn(50, 10, t0.AddMinutes(2));

        s.TurnCount.Should().Be(2);
        s.TotalInputTokens.Should().Be(150);
        s.TotalOutputTokens.Should().Be(30);
        s.LastActivityAt.Should().Be(t0.AddMinutes(2));
        s.CreatedAt.Should().Be(t0);
    }

    [Fact]
    public void SetState_updates_state_and_activity()
    {
        var s = AgentSession.Create(Guid.NewGuid(), Guid.NewGuid(), "a", DateTime.UtcNow).Value;
        var later = DateTime.UtcNow.AddSeconds(5);

        s.SetState(AgentSessionState.Running, later);

        s.State.Should().Be(AgentSessionState.Running);
        s.LastActivityAt.Should().Be(later);
    }

    [Fact]
    public void AgentMessage_requires_content()
    {
        AgentMessage.Create(Guid.NewGuid(), 0, "user", "", null, null, null, DateTime.UtcNow).IsFailure.Should().BeTrue();
        AgentMessage.Create(Guid.Empty, 0, "user", "{}", null, null, null, DateTime.UtcNow).IsFailure.Should().BeTrue();
        AgentMessage.Create(Guid.NewGuid(), -1, "user", "{}", null, null, null, DateTime.UtcNow).IsFailure.Should().BeTrue();
        AgentMessage.Create(Guid.NewGuid(), 0, " ", "{}", null, null, null, DateTime.UtcNow).IsFailure.Should().BeTrue();

        var ok = AgentMessage.Create(Guid.NewGuid(), 0, "assistant", "{\"role\":\"assistant\"}", 10, 2, "m", DateTime.UtcNow);
        ok.IsSuccess.Should().BeTrue();
        ok.Value.Sequence.Should().Be(0);
        ok.Value.InputTokens.Should().Be(10);
        ok.Value.OutputTokens.Should().Be(2);
        ok.Value.ModelId.Should().Be("m");
    }
}
