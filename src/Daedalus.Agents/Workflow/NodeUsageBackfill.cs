using Daedalus.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Thalos.Workflow;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     Writes the <see cref="WorkflowRunRecord.NodeUsageKind"/> records that are missing for completions already in the
///     workflow event log: those of every run from before phase 2.8, and any a crash lost between a completion and its
///     append. It runs once per boot of a workflow-enabled host, before the outbox poller starts, and writes nothing for a
///     (run, seq) that already has a record, so a second boot writes nothing.
/// </summary>
/// <remarks>
///     <para>
///     <b>Not an EF migration (amendment A4).</b> <c>Daedalus.Migrations</c> runs the EF migrations before the Thalos
///     ones, so on a fresh database <c>workflow_run_event</c> does not exist when an EF migration runs. A workflow-enabled
///     host starts only after both have run.
///     </para>
///     <para>
///     <b>Idempotency rests on the database.</b> The unique index <see cref="WorkflowRunRecord.NodeUsageIndexName"/>
///     refuses a second record for a (run, seq), and <see cref="NodeUsageRecorder"/> reads that refusal as "already
///     recorded". The list of existing records read first only saves the append round trips.
///     </para>
///     <para>
///     A failure is logged and the boot continues. The records feed cost analytics, and a missing figure there is a
///     smaller harm than a host that will not start. The next boot tries again.
///     </para>
/// </remarks>
internal sealed partial class NodeUsageBackfill(
    NpgsqlDataSource dataSource,
    IWorkflowStore store,
    IWorkflowRunHistory history,
    IServiceScopeFactory scopes,
    NodeUsageRecorder recorder,
    ILogger<NodeUsageBackfill> logger) : IHostedService
{
    /// <summary>The runs that have at least one event with usage. Only <c>run_id</c> and <c>usage</c> are read here.</summary>
    internal const string RunsWithUsageSql = "SELECT DISTINCT run_id FROM workflow_run_event WHERE usage IS NOT NULL";

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var written = await BackfillAsync(cancellationToken).ConfigureAwait(false);
            LogBackfilled(logger, written);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogBackfillFailed(logger, ex);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Writes every missing record. Returns how many were written.</summary>
    internal async Task<int> BackfillAsync(CancellationToken ct)
    {
        var written = 0;
        foreach (var runId in await RunsWithUsageAsync(ct).ConfigureAwait(false))
        {
            written += await BackfillRunAsync(runId, ct).ConfigureAwait(false);
        }

        return written;
    }

    private async Task<List<Guid>> RunsWithUsageAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(RunsWithUsageSql);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var ids = new List<Guid>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    private async Task<int> BackfillRunAsync(Guid runId, CancellationToken ct)
    {
        IReadOnlyList<WorkflowRunRecord> existing;
        await using (var scope = scopes.CreateAsyncScope())
        {
            existing = await scope.ServiceProvider.GetRequiredService<IWorkflowRunRecordStore>()
                .ListAsync(runId, WorkflowRunRecord.NodeUsageKind, ct).ConfigureAwait(false);
        }

        var recorded = existing.Select(r => r.Seq).ToHashSet();
        var run = await store.FindAsync(runId, ct).ConfigureAwait(false);
        var written = 0;
        foreach (var completion in await history.ListEventsAsync(runId, ct).ConfigureAwait(false))
        {
            // The same filter the run view applies: a completion of a node that ran an agent turn.
            if (completion.Usage is not { } usage || completion.FromNode is null || !recorded.Add(completion.Seq))
            {
                continue;
            }

            if (await recorder.RecordAsync(
                    runId, completion.Seq, completion.FromNode, run?.StartedBy?.Id, usage, completion.CreatedAt.UtcDateTime, ct)
                .ConfigureAwait(false))
            {
                written++;
            }
        }

        return written;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Node-usage backfill wrote {Count} record(s)")]
    private static partial void LogBackfilled(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Node-usage backfill failed; cost analytics misses the unrecorded completions until the next boot")]
    private static partial void LogBackfillFailed(ILogger logger, Exception exception);
}
