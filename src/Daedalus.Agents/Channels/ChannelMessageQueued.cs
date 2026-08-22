using ZeroAlloc.Outbox;

namespace Daedalus.Agents.Channels;

/// <summary>
///     An outbound chat reply queued for durable delivery. Designed to be written inside the same transaction
///     as the agent turn that produced it (see <see cref="IOutboxWriter{T}.WriteAsync"/>'s <c>transaction</c>
///     parameter), so that a host crash between "the agent decided what to say" and "the channel actually sent
///     it" cannot silently drop the reply — it would survive as a <c>Pending</c> row and the
///     <c>OutboxWorkerService</c> would deliver it once the host is back up. Nothing writes one yet in this
///     phase; see <see cref="ChannelMessageQueuedDispatcher"/>'s remarks.
/// </summary>
/// <param name="ChannelId">The channel adapter that owns the conversation (for example <c>telegram</c>).</param>
/// <param name="ConversationId">The external chat id within <paramref name="ChannelId"/> (for example a Telegram chat id).</param>
/// <param name="Text">The reply text to send.</param>
/// <remarks>
///     <c>[OutboxMessage]</c> triggers ZeroAlloc.Outbox's source generator, which emits
///     <c>IOutboxWriter&lt;ChannelMessageQueued&gt;</c> and the DI extension <c>AddChannelMessageQueuedOutbox()</c>
///     (confirmed by reading the generator's own <c>OutboxCodeWriter.DiExtensionMethodName</c>, which formats
///     <c>$"Add{typeName}Outbox"</c> — not assumed from the README alone).
/// </remarks>
/// <remarks>
///     Originally also carried a <c>ReplaceMessageId</c> field for editing an existing channel-native message in
///     place. Removed (Task 5 review): <see cref="Thalos.IChannelAdapter.DeliverAsync"/> takes only a
///     <see cref="Thalos.ConversationId"/> and an <see cref="Thalos.AgentEvent"/> — there is no seam through which a
///     dispatcher could pass a target message id — and nothing in <c>src/</c> ever read the field. Dead API through
///     an unusable seam; no migration needed, since ZeroAlloc.Outbox stores this record as an opaque serialized
///     <c>Payload</c> blob (see <c>AddChannelMessageOutbox</c>'s <c>OutboxMessages</c> table), not per-field columns.
/// </remarks>
[OutboxMessage]
public sealed record ChannelMessageQueued(string ChannelId, string ConversationId, string Text);
