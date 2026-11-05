using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Domain.Entities;

/// <summary>
///     Unit tests for the <see cref="ChannelConversation"/> aggregate (binds an external chat to the Thalos session
///     currently serving it).
/// </summary>
public sealed class ChannelConversationTests
{
    private static readonly DateTime _now = new(2026, 8, 20, 9, 30, 15, DateTimeKind.Utc);
    private static readonly Guid _sessionId = new(0x6f9b1c2a, 0x4d3e, 0x4f5a, 0x8b, 0x6c, 0x1a, 0x2b, 0x3c, 0x4d, 0x5e, 0x6f);
    private static readonly Guid _agentId = new(0x9e8d7c6b, 0x5a4f, 0x4321, 0x98, 0x76, 0xfe, 0xdc, 0xba, 0x09, 0x87, 0x65);

    [Fact]
    public void Create_rejects_a_blank_channel_id()
    {
        var result = ChannelConversation.Create(" ", "482910337", _sessionId, _agentId, _now);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Channel id");
    }

    [Fact]
    public void Create_accepts_an_empty_conversation_id()
    {
        // Deliberately NOT a "blank conversation id is rejected" rule (that rule was dropped): Thalos's
        // ConversationId.Value normalises a defaulted struct to "" on read, and PostgresConversationMap's
        // IConversationMap contract requires that exact empty string to round-trip through Bind/Get. ChannelId
        // keeps its non-blank rule (see Create_rejects_a_blank_channel_id) - only ConversationId's changed.
        var result = ChannelConversation.Create("telegram", "", _sessionId, _agentId, _now);

        result.IsSuccess.Should().BeTrue();
        result.Value.ConversationId.Should().Be(string.Empty);
    }

    [Fact]
    public void Create_rejects_a_null_conversation_id()
    {
        var result = ChannelConversation.Create("telegram", null!, _sessionId, _agentId, _now);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Conversation id");
    }

    [Fact]
    public void Create_rejects_an_empty_session_id()
    {
        var result = ChannelConversation.Create("telegram", "482910337", Guid.Empty, _agentId, _now);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Session id");
    }

    [Fact]
    public void Create_rejects_an_empty_agent_id()
    {
        var result = ChannelConversation.Create("telegram", "482910337", _sessionId, Guid.Empty, _now);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Agent id");
    }

    [Fact]
    public void Create_round_trips_every_field()
    {
        var conversation = ChannelConversation.Create("telegram", "482910337", _sessionId, _agentId, _now).Value;

        conversation.Id.Should().NotBe(Guid.Empty);
        conversation.ChannelId.Should().Be("telegram");
        conversation.ConversationId.Should().Be("482910337");
        conversation.SessionId.Should().Be(_sessionId);
        conversation.AgentId.Should().Be(_agentId);
        conversation.CreatedAt.Should().Be(_now);
        conversation.LastActivityAt.Should().Be(_now);
    }

    [Fact]
    public void Create_stamps_utc_timestamps()
    {
        var conversation = ChannelConversation.Create("telegram", "482910337", _sessionId, _agentId, _now).Value;

        conversation.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
        conversation.LastActivityAt.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void Two_distinct_creates_get_distinct_surrogate_ids()
    {
        var first = ChannelConversation.Create("telegram", "482910337", _sessionId, _agentId, _now).Value;
        var second = ChannelConversation.Create("telegram", "111222333", _sessionId, _agentId, _now).Value;

        first.Id.Should().NotBe(second.Id);
    }

    /// <summary>
    ///     A stored row loads as it is, even one <see cref="ChannelConversation.Create"/> would refuse today: a blank
    ///     channel id, an over-long conversation id, empty session and agent ids and a local time.
    ///     Red: validate in <c>Rehydrate</c>, or drop any one assignment.
    /// </summary>
    [Fact]
    public void Rehydrate_keeps_every_value_including_ones_Create_rejects()
    {
        var id = Guid.NewGuid();
        var local = DateTime.SpecifyKind(_now, DateTimeKind.Local);
        var lastActive = local.AddMinutes(7);
        var longConversationId = new string('c', ChannelConversation.MaxConversationIdLength + 1);
        ChannelConversation.Create("  ", longConversationId, Guid.Empty, Guid.Empty, local).IsFailure
            .Should().BeTrue("the values below are ones Create refuses");

        var conversation = ChannelConversation.Rehydrate(id, "  ", longConversationId, Guid.Empty, Guid.Empty, local, lastActive);

        conversation.Id.Should().Be(id);
        conversation.ChannelId.Should().Be("  ");
        conversation.ConversationId.Should().Be(longConversationId);
        conversation.SessionId.Should().Be(Guid.Empty);
        conversation.AgentId.Should().Be(Guid.Empty);
        conversation.CreatedAt.Should().Be(local);
        conversation.CreatedAt.Kind.Should().Be(DateTimeKind.Local);
        conversation.LastActivityAt.Should().Be(lastActive);
    }
}
