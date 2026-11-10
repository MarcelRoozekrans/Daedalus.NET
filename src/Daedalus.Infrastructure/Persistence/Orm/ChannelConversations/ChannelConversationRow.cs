using Daedalus.Domain.Entities;

namespace Daedalus.Infrastructure.Persistence.Orm.ChannelConversations;

/// <summary>One <c>"ChannelConversations"</c> row, every column, as <see cref="ChannelConversationSql"/> reads it.</summary>
internal sealed record ChannelConversationRow(
    Guid Id,
    string ChannelId,
    string ConversationId,
    Guid SessionId,
    Guid AgentId,
    DateTime CreatedAt,
    DateTime LastActivityAt)
{
    /// <summary>The binding the row holds, as stored: <see cref="ChannelConversation.Rehydrate"/> validates nothing.</summary>
    /// <returns>The domain binding.</returns>
    public ChannelConversation ToDomain() =>
        ChannelConversation.Rehydrate(Id, ChannelId, ConversationId, SessionId, AgentId, CreatedAt, LastActivityAt);
}
