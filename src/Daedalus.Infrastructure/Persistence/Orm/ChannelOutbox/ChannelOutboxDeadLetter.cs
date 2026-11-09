namespace Daedalus.Infrastructure.Persistence.Orm.ChannelOutbox;

/// <summary>A dead-lettered message in the channel outbox table, as <see cref="ChannelOutboxSql"/> reads it.</summary>
/// <param name="Id">The message id, which the store's requeue takes.</param>
/// <param name="Payload">The serialized message.</param>
public sealed record ChannelOutboxDeadLetter(Guid Id, ReadOnlyMemory<byte> Payload);

/// <summary>One dead-letter row as the generated query reads it; the payload column is a <c>bytea</c>.</summary>
/// <param name="Id">The message id.</param>
/// <param name="Payload">The serialized message.</param>
internal sealed record ChannelOutboxDeadLetterRow(Guid Id, byte[] Payload)
{
    /// <summary>The row as the public read model.</summary>
    /// <returns>The dead letter.</returns>
    public ChannelOutboxDeadLetter ToDeadLetter() => new(Id, Payload);
}
