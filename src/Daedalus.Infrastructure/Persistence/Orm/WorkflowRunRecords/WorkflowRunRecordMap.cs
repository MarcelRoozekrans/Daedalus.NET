using Daedalus.Domain.Entities;
using ZeroAlloc.ORM;

namespace Daedalus.Infrastructure.Persistence.Orm.WorkflowRunRecords;

/// <summary>
///     Maps <see cref="WorkflowRunRecord"/> to <c>"WorkflowRunRecords"</c>. Every column carries its property's name, so
///     the map lists only <see cref="WorkflowRunRecord.PayloadJson"/>, a <c>jsonb</c> document.
/// </summary>
[TableMap(typeof(WorkflowRunRecord), "WorkflowRunRecords")]
[ColumnMap(nameof(WorkflowRunRecord.PayloadJson), Json = true)]
internal static class WorkflowRunRecordMap;
