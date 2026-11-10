using ZeroAlloc.ORM;

namespace Daedalus.Infrastructure.Persistence.Orm.ScheduledRuns;

/// <summary>
///     Maps <see cref="StoredScheduledRun"/> to <c>"ScheduledRuns"</c>. Every column carries its property's name, and
///     <c>"Origin"</c> is stored by the enum member's name, as EF Core stored it. The table has no version column: the
///     guarded writes compare PostgreSQL's <c>xmin</c> through <c>Sql.Xmin</c>, EF Core's shadow token.
/// </summary>
[TableMap(typeof(StoredScheduledRun), "ScheduledRuns")]
[ColumnMap(nameof(StoredScheduledRun.Origin), EnumAsString = true)]
internal static class ScheduledRunMap;
