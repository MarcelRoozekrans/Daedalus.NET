using Microsoft.Extensions.Logging;
using Thalos;
using ZeroAlloc.Outbox;

namespace Daedalus.Agents.Channels;

/// <summary>
///     Delivers a durably-queued <see cref="ChannelMessageQueued"/> outbox message by resolving the
///     <see cref="IChannelAdapter"/> whose <see cref="IChannelAdapter.ChannelId"/> matches the message's
///     <see cref="ChannelMessageQueued.ChannelId"/> and handing it off through <see cref="IChannelAdapter.DeliverAsync"/>.
///     Registered as <c>IOutboxDispatcher&lt;ChannelMessageQueued&gt;</c>, replacing ZeroAlloc.Outbox's throwing
///     <c>DefaultOutboxDispatcher</c> fallback (see the remark on <see cref="ChannelOutboxServiceCollectionExtensions"/>).
/// </summary>
/// <remarks>
///     <para>
///     <b>Text becomes a <see cref="TextDeltaEvent"/>.</b> Of <see cref="AgentEvent"/>'s subclasses in
///     <c>Thalos.NET.Abstractions</c> (0.4.0), <see cref="TextDeltaEvent"/> is the one that represents a delivered
///     chunk of assistant text — the others describe tool calls, turn/usage bookkeeping, or memory subsystem events,
///     none of which fit a queued chat reply.
///     </para>
///     <para>
///     <b><see cref="AgentEvent.SessionId"/> and <see cref="AgentEvent.TurnId"/> are <see cref="Guid.Empty"/>.</b>
///     <see cref="ChannelMessageQueued"/> deliberately carries neither (Task 4): it is written from inside the
///     transaction that persisted the agent's turn, and by the time the outbox worker calls this dispatcher — possibly
///     after a host restart — that turn's live session may no longer exist. Fabricating a fresh
///     <c>Guid.NewGuid()</c> per delivery would manufacture a session/turn identity that never occurred and would
///     actively mislead anything that correlates events by those ids (a search for "that session" would find
///     nothing else, looking like a real id while meaning nothing). <see cref="Guid.Empty"/> is instead an explicit,
///     deterministic sentinel: any consumer that cares can recognise "this event did not come from a live turn"
///     on sight, and the value does not change between retries of the same message.
///     </para>
///     <para>
///     <b>An unknown <see cref="ChannelMessageQueued.ChannelId"/> is logged and treated as handled, not thrown.</b>
///     A missing adapter registration is a permanent condition for that message — retrying will never make the
///     adapter appear — so throwing would only burn the outbox's entire retry budget (with its exponential backoff)
///     before dead-lettering something that could never have succeeded. Logging at <see cref="LogLevel.Error"/> and
///     returning a completed <see cref="ValueTask"/> surfaces the misconfiguration immediately without wasting
///     retries or blocking the delivery of other, deliverable messages behind it.
///     </para>
/// </remarks>
public sealed partial class ChannelMessageQueuedDispatcher : IOutboxDispatcher<ChannelMessageQueued>
{
    private readonly Dictionary<string, IChannelAdapter> _adaptersByChannelId;
    private readonly ILogger<ChannelMessageQueuedDispatcher> _logger;

    public ChannelMessageQueuedDispatcher(IEnumerable<IChannelAdapter> adapters, ILogger<ChannelMessageQueuedDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var lookup = new Dictionary<string, IChannelAdapter>(StringComparer.Ordinal);
        foreach (var adapter in adapters)
        {
            lookup[adapter.ChannelId] = adapter;
        }

        _adaptersByChannelId = lookup;
    }

    /// <inheritdoc />
    public ValueTask DispatchAsync(ChannelMessageQueued message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!_adaptersByChannelId.TryGetValue(message.ChannelId, out var adapter))
        {
            LogUnknownChannel(_logger, message.ChannelId, message.ConversationId);
            return ValueTask.CompletedTask;
        }

        var conversationId = new ConversationId(message.ConversationId);
        var agentEvent = new TextDeltaEvent(new SessionId(Guid.Empty), new TurnId(Guid.Empty), message.Text);
        return adapter.DeliverAsync(conversationId, agentEvent, ct);
    }

    [LoggerMessage(EventId = 430, Level = LogLevel.Error,
        Message = "No IChannelAdapter registered for ChannelId {ChannelId}; dropping queued message for conversation {ConversationId}")]
    private static partial void LogUnknownChannel(ILogger logger, string channelId, string conversationId);
}
