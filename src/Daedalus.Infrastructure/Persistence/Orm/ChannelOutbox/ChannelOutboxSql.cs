using System.Data.Async;
using ZeroAlloc.ORM;
using ZeroAlloc.ORM.Linq;

namespace Daedalus.Infrastructure.Persistence.Orm.ChannelOutbox;

/// <summary>
///     Reads the channel outbox table <c>"OutboxMessages"</c> over one open connection, for the callers that need what
///     ZeroAlloc.Outbox.Orm's store does not offer: finding a message by what is inside its payload.
/// </summary>
/// <remarks>
///     Read-only. Every write to the table goes through the store, so its lease and retry rules hold. The table has
///     no correlation column, so a caller narrows by type, status and age here and deserializes the payloads itself.
/// </remarks>
public sealed partial class ChannelOutboxSql(IAsyncDbConnection connection)
{
    /// <summary>The store's status code of a dead-lettered message.</summary>
    private const int DeadLetterStatus = 2;

    /// <summary>
    ///     The dead-lettered messages of type <paramref name="typeName"/> enqueued at or after
    ///     <paramref name="since"/>, newest first, at most <paramref name="limit"/>.
    /// </summary>
    /// <param name="typeName">The full name of the message type.</param>
    /// <param name="since">The oldest enqueue time to read, in UTC.</param>
    /// <param name="limit">The most rows to read.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The messages' ids and payloads.</returns>
    public async Task<IReadOnlyList<ChannelOutboxDeadLetter>> ListDeadLettersAsync(
        string typeName, DateTimeOffset since, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(typeName);
        var rows = await ListDeadLetters(typeName, since, limit, ct).ConfigureAwait(false);
        return rows.ConvertAll(row => row.ToDeadLetter());
    }

    [LinqQuery(nameof(ListDeadLettersQuery))]
    private partial Task<List<ChannelOutboxDeadLetterRow>> ListDeadLetters(
        string typeName, DateTimeOffset since, int limit, CancellationToken ct);

    private static IQuery<ChannelOutboxDeadLetterRow> ListDeadLettersQuery(string typeName, DateTimeOffset since, int limit) =>
        Sql.Table<ChannelOutboxMessage>()
            .Where(m => string.Equals(m.TypeName, typeName, StringComparison.Ordinal)
                && m.Status == DeadLetterStatus
                && m.CreatedAt >= since)
            .OrderByDescending(m => m.CreatedAt)
            .Take(limit)
            .Select(m => new ChannelOutboxDeadLetterRow(m.Id, m.Payload));
}
