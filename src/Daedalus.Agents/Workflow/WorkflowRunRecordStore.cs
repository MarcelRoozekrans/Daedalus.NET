using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence.Orm;
using Daedalus.Infrastructure.Persistence.Orm.WorkflowRunRecords;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     <see cref="IWorkflowRunRecordStore"/> over <see cref="WorkflowRunRecordSql"/> (table <c>WorkflowRunRecords</c>).
///     One pooled connection per call, so the store is safe as a singleton.
/// </summary>
/// <remarks>
///     A database failure is thrown as Npgsql raised it. A duplicate on one of the partial unique indexes is a
///     <c>PostgresException</c> with <c>SqlState</c> 23505 and the index as its <c>ConstraintName</c>, which
///     <see cref="NodeUsageRecorder"/> and <see cref="NodeSelectionRunner"/> read as "already recorded".
/// </remarks>
internal sealed class WorkflowRunRecordStore(DaedalusDb db) : IWorkflowRunRecordStore
{
    /// <inheritdoc />
    public async ValueTask AppendAsync(WorkflowRunRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);

        await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        await new WorkflowRunRecordSql(connection).InsertAsync(record, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<WorkflowRunRecord>> ListAsync(Guid runId, string? kind, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        return await new WorkflowRunRecordSql(connection).ListAsync(runId, kind, ct).ConfigureAwait(false);
    }
}
