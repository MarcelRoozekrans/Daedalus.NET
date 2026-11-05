using Daedalus.Infrastructure.Persistence.Sql;
using Npgsql;

namespace Daedalus.Tests.Integration.Fixtures;

/// <summary>
///     A <see cref="DaedalusDb"/> over a database other than the shared fixture's, such as a test's scratch database,
///     with the data source it owns. Dispose it before dropping the database, so its pooled connections close.
/// </summary>
internal sealed class ScratchDaedalusDb(string connectionString) : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource = NpgsqlDataSource.Create(connectionString);

    /// <summary>Gets the <see cref="DaedalusDb"/> over the database.</summary>
    public DaedalusDb Db => new(_dataSource);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
