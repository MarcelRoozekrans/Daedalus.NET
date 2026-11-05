using ZeroAlloc.ORM;
using DomainTask = Daedalus.Domain.Entities.Task;

namespace Daedalus.Infrastructure.Persistence.Orm.Tasks;

/// <summary>
///     Maps <see cref="DomainTask"/> to <c>"Tasks"</c>. Every column carries its property's name, and the enums are
///     stored as their integers, as EF Core stored them. <see cref="DomainTask.Dependencies"/> and
///     <see cref="DomainTask.FilesToModify"/>, hand-written views over private lists, are the two <c>text[]</c> columns;
///     <see cref="DomainTask.RowVersion"/> is PostgreSQL's <c>xmin</c>, the concurrency token of every guarded write.
///     <see cref="DomainTask.Executions"/> is not a column: the executions are rows of <c>"TaskExecutions"</c>.
/// </summary>
[TableMap(typeof(DomainTask), "Tasks")]
[ColumnMap(nameof(DomainTask.Dependencies))]
[ColumnMap(nameof(DomainTask.FilesToModify))]
[ColumnMap(nameof(DomainTask.RowVersion), Column = "xmin")]
internal static class TaskMap;
