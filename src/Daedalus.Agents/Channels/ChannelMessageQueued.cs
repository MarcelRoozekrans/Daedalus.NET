using ZeroAlloc.Outbox;

namespace Daedalus.Agents.Channels;

/// <summary>
///     An outbound chat reply queued for durable delivery. Written inside the same transaction as the
///     agent turn that produced it (see <see cref="IOutboxWriter{T}.WriteAsync"/>'s <c>transaction</c>
///     parameter), so a host crash between "the agent decided what to say" and "the channel actually sent
///     it" cannot silently drop the reply — it survives as a <c>Pending</c> row and the
///     <c>OutboxWorkerService</c> delivers it once the host is back up.
/// </summary>
/// <param name="ChannelId">The channel adapter that owns the conversation (for example <c>telegram</c>).</param>
/// <param name="ConversationId">The external chat id within <paramref name="ChannelId"/> (for example a Telegram chat id).</param>
/// <param name="Text">The reply text to send.</param>
/// <param name="ReplaceMessageId">
///     When set, the channel-native id of a message this reply should edit in place instead of sending a new
///     one (streaming/progress updates); <see langword="null"/> sends a new message.
/// </param>
/// <remarks>
///     <c>[OutboxMessage]</c> triggers ZeroAlloc.Outbox's source generator, which emits
///     <c>IOutboxWriter&lt;ChannelMessageQueued&gt;</c> and the DI extension <c>AddChannelMessageQueuedOutbox()</c>
///     (confirmed by reading the generator's own <c>OutboxCodeWriter.DiExtensionMethodName</c>, which formats
///     <c>$"Add{typeName}Outbox"</c> — not assumed from the README alone).
/// </remarks>
[OutboxMessage]
public sealed record ChannelMessageQueued(string ChannelId, string ConversationId, string Text, string? ReplaceMessageId);
