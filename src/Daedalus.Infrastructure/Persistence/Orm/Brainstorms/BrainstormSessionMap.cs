using Daedalus.Domain.Entities;
using ZeroAlloc.ORM;

namespace Daedalus.Infrastructure.Persistence.Orm.Brainstorms;

/// <summary>
///     Maps <see cref="BrainstormSession"/> to <c>"BrainstormSessions"</c>. Every column carries its property's name, and
///     <see cref="BrainstormSession.Phase"/> is stored as its integer, as EF Core stored it.
///     <see cref="BrainstormSession.RowVersion"/> is PostgreSQL's <c>xmin</c>, the concurrency token of the guarded save,
///     not the dead <c>bytea</c> <c>"RowVersion"</c> column, which migration 2 drops.
///     <see cref="BrainstormSession.Messages"/> is not a column: the messages are rows of <c>"BrainstormMessages"</c>.
/// </summary>
[TableMap(typeof(BrainstormSession), "BrainstormSessions")]
[ColumnMap(nameof(BrainstormSession.RowVersion), Column = "xmin")]
internal static class BrainstormSessionMap;
