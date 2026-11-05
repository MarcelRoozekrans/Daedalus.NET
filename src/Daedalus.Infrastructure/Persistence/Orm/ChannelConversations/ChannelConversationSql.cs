using System.Data.Async;
using ZeroAlloc.ORM;
using ZeroAlloc.ORM.Linq;
using ChannelConversation = Daedalus.Domain.Entities.ChannelConversation;

namespace Daedalus.Infrastructure.Persistence.Orm.ChannelConversations;

/// <summary>
///     The SQL of the <c>"ChannelConversations"</c> table, over one open connection. Its public methods take and
///     return <see cref="ChannelConversation"/>; the generated queries, their companions and the row record stay
///     inside.
/// </summary>
/// <remarks>
///     Every lookup is keyed on the natural key, <c>("ChannelId", "ConversationId")</c>, which a unique index
///     enforces. The write is a genuine database upsert, raw SQL because the ORM has no LINQ upsert (ruling R10 and
///     the ruling for this table): two processes can race to bind one conversation, and only
///     <c>ON CONFLICT</c> closes that window. The conflict branch never touches <c>"Id"</c> or <c>"CreatedAt"</c>,
///     so a rebind keeps the row's identity and creation time. There is no concurrency guard to keep: the row has
///     no version column and last writer wins, as before.
/// </remarks>
public sealed partial class ChannelConversationSql(IAsyncDbConnection connection)
{
    /// <summary>
    ///     Binds the conversation of <paramref name="binding"/> to its session, inserting a row or, when the channel
    ///     and conversation are bound already, replacing the session, agent and activity time of the row.
    /// </summary>
    /// <param name="binding">The binding to store.</param>
    /// <param name="ct">Cancels the write.</param>
    /// <returns>A task that completes when the row is written.</returns>
    public Task UpsertAsync(ChannelConversation binding, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return Upsert(
            binding.Id, binding.ChannelId, binding.ConversationId, binding.SessionId, binding.AgentId,
            binding.CreatedAt, binding.LastActivityAt, ct);
    }

    /// <summary>Reads the binding of one conversation of one channel.</summary>
    /// <param name="channelId">The channel that owns the conversation.</param>
    /// <param name="conversationId">The channel's own conversation id.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The binding as stored, or null when the conversation is not bound.</returns>
    public async Task<ChannelConversation?> FindAsync(string channelId, string conversationId, CancellationToken ct)
    {
        var row = await FindRow(channelId, conversationId, ct).ConfigureAwait(false);
        return row?.ToDomain();
    }

    /// <summary>
    ///     Deletes the binding of one conversation of one channel. Both halves of the key are in the predicate, so
    ///     an identically named conversation of another channel stays. An absent binding is not an error.
    /// </summary>
    /// <param name="channelId">The channel that owns the conversation.</param>
    /// <param name="conversationId">The channel's own conversation id.</param>
    /// <param name="ct">Cancels the delete.</param>
    /// <returns>A task that completes when the row is gone.</returns>
    public Task DeleteAsync(string channelId, string conversationId, CancellationToken ct) =>
        Delete(channelId, conversationId, ct);

    [Command("""
        INSERT INTO "ChannelConversations"
            ("Id", "ChannelId", "ConversationId", "SessionId", "AgentId", "CreatedAt", "LastActivityAt")
        VALUES (@id, @channelId, @conversationId, @sessionId, @agentId, @createdAt, @lastActivityAt)
        ON CONFLICT ("ChannelId", "ConversationId")
        DO UPDATE SET "SessionId" = EXCLUDED."SessionId", "AgentId" = EXCLUDED."AgentId",
                      "LastActivityAt" = EXCLUDED."LastActivityAt"
        """)]
    private partial Task<int> Upsert(
        Guid id, string channelId, string conversationId, Guid sessionId, Guid agentId, DateTime createdAt,
        DateTime lastActivityAt, CancellationToken ct);

    [LinqQuery(nameof(FindRowQuery))]
    private partial Task<ChannelConversationRow?> FindRow(string channelId, string conversationId, CancellationToken ct);

    private static IQuery<ChannelConversationRow> FindRowQuery(string channelId, string conversationId) =>
        Sql.Table<ChannelConversation>()
            .Where(c => string.Equals(c.ChannelId, channelId, StringComparison.Ordinal)
                && string.Equals(c.ConversationId, conversationId, StringComparison.Ordinal))
            .Select(c => new ChannelConversationRow(
                c.Id, c.ChannelId, c.ConversationId, c.SessionId, c.AgentId, c.CreatedAt, c.LastActivityAt));

    [LinqCommand(nameof(DeleteQuery))]
    private partial Task<int> Delete(string channelId, string conversationId, CancellationToken ct);

    private static int DeleteQuery(string channelId, string conversationId) =>
        Sql.Table<ChannelConversation>()
            .Where(c => string.Equals(c.ChannelId, channelId, StringComparison.Ordinal)
                && string.Equals(c.ConversationId, conversationId, StringComparison.Ordinal))
            .ExecuteDelete();
}
