using Daedalus.Agents.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos;

namespace Daedalus.Tests.Unit.Application.Channels;

/// <summary>
///     Unit tests for <see cref="ChannelMessageQueuedDispatcher"/>: it must resolve the right
///     <see cref="IChannelAdapter"/> by <see cref="ChannelMessageQueued.ChannelId"/>, pass the
///     <see cref="Thalos.ConversationId"/> through unchanged, and never throw for an unregistered channel
///     (the outbox would otherwise retry a permanently-undeliverable message to exhaustion).
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
        var message = new ChannelMessageQueued("telegram", TelegramConversationId, "Deploy finished: build 4821 is live.", null);

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
        var message = new ChannelMessageQueued("telegram", TelegramConversationId, "Deploy finished: build 4821 is live.", null);

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
        var message = new ChannelMessageQueued("telegram", TelegramConversationId, "Deploy finished: build 4821 is live.", null);

        await sut.DispatchAsync(message, CancellationToken.None);

        await telegram.Received(1).DeliverAsync(
            Arg.Any<Thalos.ConversationId>(),
            Arg.Is<AgentEvent>(e => e is TextDeltaEvent && ((TextDeltaEvent)e).Text == "Deploy finished: build 4821 is live."),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unknown_ChannelId_does_not_throw_and_does_not_reach_any_registered_adapter()
    {
        var telegram = Substitute.For<IChannelAdapter>();
        telegram.ChannelId.Returns("telegram");
        var console = Substitute.For<IChannelAdapter>();
        console.ChannelId.Returns("console");

        var sut = new ChannelMessageQueuedDispatcher([telegram, console], NullLogger<ChannelMessageQueuedDispatcher>.Instance);
        var message = new ChannelMessageQueued("whatsapp", TelegramConversationId, "Deploy finished: build 4821 is live.", null);

        var act = async () => await sut.DispatchAsync(message, CancellationToken.None);

        await act.Should().NotThrowAsync();
        await telegram.DidNotReceiveWithAnyArgs().DeliverAsync(default!, default!, default);
        await console.DidNotReceiveWithAnyArgs().DeliverAsync(default!, default!, default);
    }
}
