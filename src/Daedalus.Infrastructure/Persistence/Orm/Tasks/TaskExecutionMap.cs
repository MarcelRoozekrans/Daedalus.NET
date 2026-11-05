using Daedalus.Domain.Entities;
using ZeroAlloc.ORM;

namespace Daedalus.Infrastructure.Persistence.Orm.Tasks;

/// <summary>
///     Maps <see cref="TaskExecution"/> to <c>"TaskExecutions"</c>. Every column carries its property's name;
///     <see cref="TaskExecution.ExecutionDuration"/> is the <c>interval</c> column, which reads as a <see cref="TimeSpan"/>.
/// </summary>
[TableMap(typeof(TaskExecution), "TaskExecutions")]
internal static class TaskExecutionMap;
