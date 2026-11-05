using System.Data.Async;
using ZeroAlloc.ORM;
using ZeroAlloc.ORM.Linq;
using WorkflowRunRecord = Daedalus.Domain.Entities.WorkflowRunRecord;

namespace Daedalus.Infrastructure.Persistence.Orm.WorkflowRunRecords;

/// <summary>
///     The SQL of the <c>"WorkflowRunRecords"</c> table, over one open connection. Its public methods take and return
///     <see cref="WorkflowRunRecord"/>; the generated queries, their companions and the row record stay inside.
/// </summary>
/// <remarks>
///     The table is insert-only, so there is no update or delete here and no concurrency guard to keep. The insert is
///     raw SQL, as the ORM has no LINQ insert (ruling R10); it lists every column but the database-generated
///     <c>"Id"</c>. A duplicate on a partial unique index surfaces as Npgsql's <c>PostgresException</c>, unwrapped.
/// </remarks>
public sealed partial class WorkflowRunRecordSql(IAsyncDbConnection connection)
{
    /// <summary>Inserts <paramref name="record"/> as a new row; the database gives it its <c>"Id"</c>.</summary>
    /// <param name="record">The record to store.</param>
    /// <param name="ct">Cancels the insert.</param>
    /// <returns>A task that completes when the row is written.</returns>
    public Task InsertAsync(WorkflowRunRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        return Insert(
            record.RunId, record.Seq, record.Node, record.Kind, record.PrincipalId, record.StartedById,
            record.PayloadJson, record.CreatedAt, ct);
    }

    /// <summary>
    ///     The records of run <paramref name="runId"/>, only those of <paramref name="kind"/> when it is not null, in
    ///     <c>"Seq"</c> order and then <c>"Id"</c> order.
    /// </summary>
    /// <param name="runId">The run whose records to read.</param>
    /// <param name="kind">The one kind to read, or null for every kind.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The records, as stored.</returns>
    public async Task<IReadOnlyList<WorkflowRunRecord>> ListAsync(Guid runId, string? kind, CancellationToken ct)
    {
        var rows = await ListRows(runId, kind, ct).ConfigureAwait(false);
        return rows.ConvertAll(row => row.ToDomain());
    }

    [Command("""
        INSERT INTO "WorkflowRunRecords"
            ("RunId", "Seq", "Node", "Kind", "PrincipalId", "StartedById", "PayloadJson", "CreatedAt")
        VALUES (@runId, @seq, @node, @kind, @principalId, @startedById, @payloadJson, @createdAt)
        """)]
    private partial Task<int> Insert(
        Guid runId, long seq, string node, string kind, string principalId, string? startedById,
        [Json] string payloadJson, DateTime createdAt, CancellationToken ct);

    [LinqQuery(nameof(ListRowsQuery))]
    private partial Task<List<WorkflowRunRecordRow>> ListRows(Guid runId, string? kind, CancellationToken ct);

    private static IQuery<WorkflowRunRecordRow> ListRowsQuery(Guid runId, string? kind) =>
        Sql.Table<WorkflowRunRecord>()
            .Where(r => r.RunId == runId && (kind == null || string.Equals(r.Kind, kind, StringComparison.Ordinal)))
            .OrderBy(r => r.Seq)
            .ThenBy(r => r.Id)
            .Select(r => new WorkflowRunRecordRow(
                r.Id, r.RunId, r.Seq, r.Node, r.Kind, r.PrincipalId, r.StartedById, r.PayloadJson, r.CreatedAt));
}
