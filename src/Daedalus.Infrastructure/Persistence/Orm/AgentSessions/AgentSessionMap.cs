using Daedalus.Domain.Entities;
using ZeroAlloc.ORM;

namespace Daedalus.Infrastructure.Persistence.Orm.AgentSessions;

/// <summary>
///     Maps <see cref="AgentSession"/> to <c>"AgentSessions"</c>. Every column carries its property's name, and
///     <see cref="AgentSession.State"/> is stored as its integer, as EF Core stored it. The table's dead <c>bytea</c>
///     <c>"RowVersion"</c> column had no property and was never read or written; migration 2 dropped it.
/// </summary>
[TableMap(typeof(AgentSession), "AgentSessions")]
internal static class AgentSessionMap;
