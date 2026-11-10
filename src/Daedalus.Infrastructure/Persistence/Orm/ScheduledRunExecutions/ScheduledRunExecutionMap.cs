using ZeroAlloc.ORM;

namespace Daedalus.Infrastructure.Persistence.Orm.ScheduledRunExecutions;

/// <summary>
///     Maps <see cref="StoredScheduledRunExecution"/> to <c>"ScheduledRunExecutions"</c>. Every column carries its
///     property's name, and <c>"Step"</c> and <c>"FailedAtStep"</c> are stored by the enum member's name, as EF Core
///     stored them. The table has no version column: the guarded writes compare PostgreSQL's <c>xmin</c> through
///     <c>Sql.Xmin</c>, EF Core's shadow token.
/// </summary>
[TableMap(typeof(StoredScheduledRunExecution), "ScheduledRunExecutions")]
[ColumnMap(nameof(StoredScheduledRunExecution.Step), EnumAsString = true)]
[ColumnMap(nameof(StoredScheduledRunExecution.FailedAtStep), EnumAsString = true)]
internal static class ScheduledRunExecutionMap;
