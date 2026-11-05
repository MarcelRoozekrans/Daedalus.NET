using System.Data.Async;
using System.Data.Async.Adapters;
using Npgsql;

namespace Daedalus.Infrastructure.Persistence.Sql;

/// <summary>
///     Opens connections to the Daedalus database for the ZeroAlloc.ORM stores, from the host's one
///     <see cref="NpgsqlDataSource"/> (ruling R4).
/// </summary>
/// <remarks>
///     A store opens one connection per call and disposes it, which returns it to the pool, as EF Core's pooled
///     contexts did; so a singleton store and a scoped one behave alike. Registered as a singleton by
///     <c>AddApplicationDatabase</c>.
/// </remarks>
public sealed class DaedalusDb(NpgsqlDataSource dataSource)
{
    /// <summary>Opens a pooled connection, adapted to the ORM's <see cref="IAsyncDbConnection"/>. The caller disposes it.</summary>
    /// <param name="ct">Cancels the open.</param>
    /// <returns>The open connection.</returns>
    public async ValueTask<IAsyncDbConnection> OpenAsync(CancellationToken ct)
    {
        var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            return connection.AsAsync();
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
