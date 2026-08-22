using Daedalus.Agents.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos;

namespace Daedalus.Tests.Unit.Application.Channels;

/// <summary>
///     Unit tests for <see cref="ChannelMessageQueuedDispatcher"/>: it must resolve the right
///     <see cref="IChannelAdapter"/> by <see cref="ChannelMessageQueued.ChannelId"/>, pass the
///     <see cref="Thalos.ConversationId"/> through unchanged, give each delivery its own <see cref="TurnId"/> so
///     consecutive queued messages don't edit one another, and never throw for an unregistered channel (the
///     outbox would otherwise retry a permanently-undeliverable message to exhaustion).
/// </summary>
public sealed class ChannelMessageQueuedDispatcherTests
{
    private const string TelegramConversationId = "482910337";

    [Fact]
    public async Task Routes_to_the_adapter_whose_ChannelId_matches_and_not_to_the_other_registered_adapter()
    {
        var telegram = Substitute.For<IChannelAdapter>();
        telegram.ChannelId.Returns("telegram");
        var console = Substitute.For<IChannelAdapter>();
        console.ChannelId.Returns("console");

        var sut = new ChannelMessageQueuedDispatcher([telegram, console], NullLogger<ChannelMessageQueuedDispatcher>.Instance);
        var message = new ChannelMessageQueued("telegram", TelegramConversationId, "Deploy finished: build 4821 is live.");

        await sut.DispatchAsync(message, CancellationToken.None);

        await telegram.Received(1).DeliverAsync(
            Arg.Any<Thalos.ConversationId>(), Arg.Any<AgentEvent>(), Arg.Any<CancellationToken>());
        await console.DidNotReceiveWithAnyArgs().DeliverAsync(
            default!, default!, default);
    }

    [Fact]
    public async Task Passes_the_ConversationId_through_unchanged()
    {
        var telegram = Substitute.For<IChannelAdapter>();
        telegram.ChannelId.Returns("telegram");

        var sut = new ChannelMessageQueuedDispatcher([telegram], NullLogger<ChannelMessageQueuedDispatcher>.Instance);
        var message = new ChannelMessageQueued("telegram", TelegramConversationId, "Deploy finished: build 4821 is live.");

        await sut.DispatchAsync(message, CancellationToken.None);

        await telegram.Received(1).DeliverAsync(
            Arg.Is<Thalos.ConversationId>(id => id.Value == TelegramConversationId), Arg.Any<AgentEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delivers_the_message_text_as_a_TextDeltaEvent()
    {
        var telegram = Substitute.For<IChannelAdapter>();
        telegram.ChannelId.Returns("telegram");

        var sut = new ChannelMessageQueuedDispatcher([telegram], NullLogger<ChannelMessageQueuedDispatcher>.Instance);
        var message = new ChannelMessageQueued("telegram", TelegramConversationId, "Deploy finished: build 4821 is live.");

        await sut.DispatchAsync(message, CancellationToken.None);

        await telegram.Received(1).DeliverAsync(
            Arg.Any<Thalos.ConversationId>(),
            Arg.Is<AgentEvent>(e => e is TextDeltaEvent && ((TextDeltaEvent)e).Text == "Deploy finished: build 4821 is live."),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     Regression test for the review finding that a constant <c>TurnId</c> made a second queued message to
    ///     the same conversation silently edit the first: an editing adapter (Telegram) tells "re-render of the
    ///     same turn" from "new, unrelated message" by comparing <c>TurnId</c>, so two independent outbox messages
    ///     — crash-recovery notice #1 and #2, say — must never carry the same one, or the second would overwrite
    ///     the first instead of arriving on its own.
    /// </summary>
    [Fact]
    public async Task Two_messages_for_the_same_conversation_carry_different_TurnIds()
    {
        var telegram = Substitute.For<IChannelAdapter>();
        telegram.ChannelId.Returns("telegram");

        // Capture the AgentEvent from each DeliverAsync call directly, rather than reading it back off
        // ReceivedCalls() (which also captures the unrelated ChannelId property-getter call the dispatcher makes
        // while building its lookup, and would misalign against a naive positional read).
        var delivered = new List<AgentEvent>();
        telegram.DeliverAsync(Arg.Any<Thalos.ConversationId>(), Arg.Do<AgentEvent>(delivered.Add), Arg.Any<CancellationToken>())
            .Returns(ValueTask.CompletedTask);

        var sut = new ChannelMessageQueuedDispatcher([telegram], NullLogger<ChannelMessageQueuedDispatcher>.Instance);
        var first = new ChannelMessageQueued("telegram", TelegramConversationId, "Recovery notice #1: host restarted.");
        var second = new ChannelMessageQueued("telegram", TelegramConversationId, "Recovery notice #2: host restarted again.");

        await sut.DispatchAsync(first, CancellationToken.None);
        await sut.DispatchAsync(second, CancellationToken.None);

        var turnIds = delivered.Select(e => ((TextDeltaEvent)e).TurnId).ToList();

        turnIds.Should().HaveCount(2);
        turnIds[0].Should().NotBe(turnIds[1],
            "a constant TurnId would make the second outbox notice look like a re-render of the first and silently overwrite it");
    }

    [Fact]
    public async Task An_unknown_ChannelId_does_not_throw_and_does_not_reach_any_registered_adapter()
    {
        var telegram = Substitute.For<IChannelAdapter>();
        telegram.ChannelId.Returns("telegram");
        var console = Substitute.For<IChannelAdapter>();
        console.ChannelId.Returns("console");

        var sut = new ChannelMessageQueuedDispatcher([telegram, console], NullLogger<ChannelMessageQueuedDispatcher>.Instance);
        var message = new ChannelMessageQueued("whatsapp", TelegramConversationId, "Deploy finished: build 4821 is live.");

        var act = async () => await sut.DispatchAsync(message, CancellationToken.None);

        await act.Should().NotThrowAsync();
        await telegram.DidNotReceiveWithAnyArgs().DeliverAsync(default!, default!, default);
        await console.DidNotReceiveWithAnyArgs().DeliverAsync(default!, default!, default);
    }
}
