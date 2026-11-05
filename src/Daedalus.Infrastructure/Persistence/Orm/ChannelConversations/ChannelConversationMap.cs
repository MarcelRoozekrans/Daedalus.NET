using Daedalus.Domain.Entities;
using ZeroAlloc.ORM;

namespace Daedalus.Infrastructure.Persistence.Orm.ChannelConversations;

/// <summary>
///     Maps <see cref="ChannelConversation"/> to <c>"ChannelConversations"</c>. Every column carries its property's
///     name and none is special, so the map names only the table.
/// </summary>
[TableMap(typeof(ChannelConversation), "ChannelConversations")]
internal static class ChannelConversationMap;
