using ZeroAlloc.ORM;
using RepositoryConfiguration = Daedalus.Domain.Entities.RepositoryConfiguration;

namespace Daedalus.Infrastructure.Persistence.Orm.RepositoryConfigurations;

/// <summary>
///     Maps <see cref="RepositoryConfiguration"/> to <c>"RepositoryConfigurations"</c>. Every column carries its
///     property's name, as EF Core stored it. The table's dead <c>bytea</c> <c>"RowVersion"</c> column had no property
///     and was never read or written; migration 2 dropped it.
/// </summary>
[TableMap(typeof(RepositoryConfiguration), "RepositoryConfigurations")]
internal static class RepositoryConfigurationMap;
