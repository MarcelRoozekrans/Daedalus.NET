using ZeroAlloc.ORM;
using Project = Daedalus.Domain.Entities.Project;

namespace Daedalus.Infrastructure.Persistence.Orm.Projects;

/// <summary>
///     Maps <see cref="Project"/> to <c>"Projects"</c>. Every column carries its property's name, as EF Core stored it;
///     <see cref="Project.RowVersion"/> is PostgreSQL's <c>xmin</c>, the concurrency token of the guarded update, not the
///     dead <c>bytea</c> <c>"RowVersion"</c> column, which migration 2 dropped. <see cref="Project.Tasks"/> is not a
///     column: the tasks are rows of <c>"Tasks"</c>, read through <see cref="Tasks.TaskMap"/>.
/// </summary>
[TableMap(typeof(Project), "Projects")]
[ColumnMap(nameof(Project.RowVersion), Column = "xmin")]
internal static class ProjectMap;
