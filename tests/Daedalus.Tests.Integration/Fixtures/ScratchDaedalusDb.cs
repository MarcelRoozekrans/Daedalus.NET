using Daedalus.Infrastructure.Persistence.Orm;
using Npgsql;

namespace Daedalus.Tests.Integration.Fixtures;

/// <summary>
///     A <see cref="DaedalusDb"/> over a database other than the shared fixture's, such as a test's scratch database,
///     with the data source it owns. Dispose it before dropping the database, so its pooled connections close.
/// </summary>
internal sealed class ScratchDaedalusDb : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    public ScratchDaedalusDb(string connectionString)
    {
        _dataSource = NpgsqlDataSource.Create(connectionString);
        Db = new DaedalusDb(_dataSource);
    }

    /// <summary>Gets the <see cref="DaedalusDb"/> over the database.</summary>
    public DaedalusDb Db { get; }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
