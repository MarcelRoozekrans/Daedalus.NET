namespace Daedalus.Infrastructure.Persistence.Orm.ChannelOutbox;

/// <summary>A dead-lettered message in the channel outbox table, as <see cref="ChannelOutboxSql"/> reads it.</summary>
/// <param name="Id">The message id, which the store's requeue takes.</param>
/// <param name="Payload">The serialized message.</param>
/// <param name="DeadLetterError">Why delivery was abandoned, if the store recorded it.</param>
/// <param name="RetryCount">How many attempts were made before it was abandoned.</param>
/// <param name="CreatedAt">When the message was enqueued, in UTC.</param>
public sealed record ChannelOutboxDeadLetter(
    Guid Id, ReadOnlyMemory<byte> Payload, string? DeadLetterError, int RetryCount, DateTimeOffset CreatedAt);

/// <summary>One dead-letter row as the generated query reads it; the payload column is a <c>bytea</c>.</summary>
/// <param name="Id">The message id.</param>
/// <param name="Payload">The serialized message.</param>
/// <param name="DeadLetterError">Why delivery was abandoned.</param>
/// <param name="RetryCount">How many attempts were made.</param>
/// <param name="CreatedAt">When the message was enqueued.</param>
internal sealed record ChannelOutboxDeadLetterRow(
    Guid Id, byte[] Payload, string? DeadLetterError, int RetryCount, DateTimeOffset CreatedAt)
{
    /// <summary>The row as the public read model.</summary>
    /// <returns>The dead letter.</returns>
    public ChannelOutboxDeadLetter ToDeadLetter() => new(Id, Payload, DeadLetterError, RetryCount, CreatedAt);
}
