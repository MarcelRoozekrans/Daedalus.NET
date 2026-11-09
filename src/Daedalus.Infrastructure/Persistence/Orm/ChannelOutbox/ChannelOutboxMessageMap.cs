using ZeroAlloc.ORM;

namespace Daedalus.Infrastructure.Persistence.Orm.ChannelOutbox;

/// <summary>
///     Maps <see cref="ChannelOutboxMessage"/> to the channel outbox table <c>"OutboxMessages"</c>, the quoted,
///     mixed-case table EF Core created. Every column carries its property's name, so the map names only the table.
/// </summary>
[TableMap(typeof(ChannelOutboxMessage), "OutboxMessages")]
internal static class ChannelOutboxMessageMap;
