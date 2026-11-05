using Daedalus.Domain.Entities;

namespace Daedalus.Infrastructure.Persistence.Sql.WorkflowRunRecords;

/// <summary>One <c>"WorkflowRunRecords"</c> row, every column, as <see cref="WorkflowRunRecordSql"/> reads it.</summary>
internal sealed record WorkflowRunRecordRow(
    long Id,
    Guid RunId,
    long Seq,
    string Node,
    string Kind,
    string PrincipalId,
    string? StartedById,
    string PayloadJson,
    DateTime CreatedAt)
{
    /// <summary>The record the row holds, as stored: <see cref="WorkflowRunRecord.Rehydrate"/> validates nothing.</summary>
    /// <returns>The domain record.</returns>
    public WorkflowRunRecord ToDomain() =>
        WorkflowRunRecord.Rehydrate(Id, RunId, Seq, Node, Kind, PrincipalId, StartedById, PayloadJson, CreatedAt);
}
