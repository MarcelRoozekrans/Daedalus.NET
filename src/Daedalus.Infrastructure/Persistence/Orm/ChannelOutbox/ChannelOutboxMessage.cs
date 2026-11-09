namespace Daedalus.Infrastructure.Persistence.Orm.ChannelOutbox;

/// <summary>
///     The columns of the channel outbox table <c>"OutboxMessages"</c> that <see cref="ChannelOutboxSql"/> reads. The
///     table belongs to ZeroAlloc.Outbox.Orm's store, which writes it; this type exists only so a LINQ chain can name
///     the columns, and nothing constructs it.
/// </summary>
internal sealed class ChannelOutboxMessage
{
    /// <summary>The message id, the primary key.</summary>
    public Guid Id { get; init; }

    /// <summary>The full name of the message type.</summary>
    public string TypeName { get; init; } = "";

    /// <summary>The serialized message.</summary>
    public byte[] Payload { get; init; } = [];

    /// <summary>The store's status code: 0 pending, 1 succeeded, 2 dead-lettered.</summary>
    public int Status { get; init; }

    /// <summary>When the message was enqueued, in UTC.</summary>
    public DateTimeOffset CreatedAt { get; init; }
}
