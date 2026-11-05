using Daedalus.Domain.Entities;
using ZeroAlloc.ORM;

namespace Daedalus.Infrastructure.Persistence.Orm.AgentMemories;

/// <summary>
///     Maps <see cref="AgentMemory"/> to <c>"AgentMemories"</c>. Every column carries its property's name;
///     <see cref="AgentMemory.Tags"/>, a hand-written view over the private list, is the <c>text[]</c> column.
/// </summary>
[TableMap(typeof(AgentMemory), "AgentMemories")]
[ColumnMap(nameof(AgentMemory.Tags))]
internal static class AgentMemoryMap;
