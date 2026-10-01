using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     <see cref="IWorkflowRunRecordStore"/> over <see cref="ApplicationDbContext"/> (table <c>WorkflowRunRecords</c>).
///     A fresh short-lived context per call, so the store is safe as a singleton, the same pattern as
///     <see cref="Charters.PostgresRoleCharterStore"/>.
/// </summary>
internal sealed class WorkflowRunRecordStore(IDbContextFactory<ApplicationDbContext> contextFactory)
    : IWorkflowRunRecordStore
{
    /// <inheritdoc />
    public async ValueTask AppendAsync(WorkflowRunRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);

        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.WorkflowRunRecords.Add(record);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<WorkflowRunRecord>> ListAsync(Guid runId, string? kind, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var query = db.WorkflowRunRecords.AsNoTracking().Where(r => r.RunId == runId);
        if (kind is not null)
            query = query.Where(r => r.Kind == kind);

        return await query
            .OrderBy(r => r.Seq)
            .ThenBy(r => r.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }
}
