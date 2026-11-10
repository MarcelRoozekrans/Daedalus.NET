using System.Data.Async;
using System.Data.Async.Adapters;
using System.Data.Common;

namespace Daedalus.Infrastructure.Persistence.Orm;

/// <summary>Bridges a ZeroAlloc.ORM transaction to the ADO.NET API that ZeroAlloc.Outbox's writers take.</summary>
public static class AsyncDbTransactionExtensions
{
    /// <summary>
    ///     The ADO.NET transaction under <paramref name="transaction"/>, for an <c>IOutboxWriter&lt;T&gt;</c>, whose
    ///     <c>WriteAsync</c> takes a <see cref="DbTransaction"/> only. The channel store inserts on that transaction's
    ///     own connection, so the outbox row commits or rolls back with the caller's other writes (ruling R114).
    /// </summary>
    /// <param name="transaction">A transaction begun on a connection <see cref="DaedalusDb"/> opened.</param>
    /// <returns>The provider's transaction.</returns>
    /// <exception cref="ArgumentException">
    ///     <paramref name="transaction"/> is not an ADO.NET adapter's, so it has no ADO.NET transaction under it.
    /// </exception>
    public static DbTransaction AsDbTransaction(this IAsyncDbTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return transaction is AdapterDbTransaction adapter
            ? (DbTransaction)adapter
            : throw new ArgumentException(
                $"A {transaction.GetType().Name} has no ADO.NET transaction under it; begin it on a DaedalusDb connection.",
                nameof(transaction));
    }
}
