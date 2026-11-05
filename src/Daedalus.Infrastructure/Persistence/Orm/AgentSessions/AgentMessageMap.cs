using Daedalus.Domain.Entities;
using ZeroAlloc.ORM;

namespace Daedalus.Infrastructure.Persistence.Orm.AgentSessions;

/// <summary>
///     Maps <see cref="AgentMessage"/> to <c>"AgentMessages"</c>. Every column carries its property's name;
///     <see cref="AgentMessage.ContentJson"/> is the <c>jsonb</c> column.
/// </summary>
[TableMap(typeof(AgentMessage), "AgentMessages")]
[ColumnMap(nameof(AgentMessage.ContentJson), Json = true)]
internal static class AgentMessageMap;
