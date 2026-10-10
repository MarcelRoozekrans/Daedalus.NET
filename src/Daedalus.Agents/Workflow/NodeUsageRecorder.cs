using Daedalus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Thalos;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     Appends one <see cref="WorkflowRunRecord.NodeUsageKind"/> record for a completed agent node, so cost analytics
///     reads a manufacture run's usage from Daedalus's own table. <see cref="ReviewHandoffWorkflowStore"/> calls it on
///     every completion, and <see cref="NodeUsageBackfill"/> calls it for completions that have no record yet.
/// </summary>
/// <remarks>
///     A second append for a (run, seq) that already has a record violates the unique index
///     <see cref="WorkflowRunRecord.NodeUsageIndexName"/>. That is the expected outcome of the live append racing the backfill,
///     or of a retried dispatch, so it reads as "already recorded": it returns false and logs at Debug.
///     A failure is logged, not thrown, and so is a cancelled caller: the append runs under a token of its own, which only
///     <c>AppendTimeout</c> cancels, never the dispatch's. The completion it describes is already persisted, so throwing would only
///     dead-letter a dispatch whose work is done. The next boot's backfill writes the missing record.
/// </remarks>
internal sealed partial class NodeUsageRecorder(IServiceScopeFactory scopes, TimeProvider clock, ILogger<NodeUsageRecorder> logger)
{
    /// <summary>The principal of every node-usage record: the host wrote it, no caller did.</summary>
    public const string HostPrincipalId = "host";

    /// <summary>How long one append may take, under a token of its own.</summary>
    private static readonly TimeSpan AppendTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Now, on the host clock, as a UTC <see cref="DateTime"/>.</summary>
    public DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    /// <summary>Appends the record. Returns whether it was written: false when the record already existed or the append failed. The caller's token is deliberately not passed to the append.</summary>
    public async ValueTask<bool> RecordAsync(
        Guid runId, long seq, string node, string? startedById, TurnUsage usage, DateTime createdAtUtc, CancellationToken ct)
    {
        var payload = new NodeUsage(
            string.IsNullOrEmpty(usage.ModelId) ? null : usage.ModelId,
            usage.InputTokens, usage.OutputTokens, usage.CacheReadTokens, usage.CacheWriteTokens).ToPayloadJson();
        var record = WorkflowRunRecord.Create(
            runId, seq, node, WorkflowRunRecord.NodeUsageKind, HostPrincipalId, startedById, payload, createdAtUtc);
        if (record.IsFailure)
        {
            LogRejected(logger, runId, seq, record.Error);
            return false;
        }

        try
        {
            using var timeout = new CancellationTokenSource(AppendTimeout, clock);
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IWorkflowRunRecordStore>()
                .AppendAsync(record.Value, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (IsAlreadyRecorded(ex))
        {
            LogAlreadyRecorded(logger, runId, seq);
            return false;
        }
        catch (Exception ex)
        {
            LogAppendFailed(logger, ex, runId, seq);
            return false;
        }
    }

    /// <summary>Whether the failure is the unique index on (run, seq) refusing a second node-usage record.</summary>
    private static bool IsAlreadyRecorded(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
        && string.Equals(pg.ConstraintName, WorkflowRunRecord.NodeUsageIndexName, StringComparison.Ordinal);

    [LoggerMessage(Level = LogLevel.Debug, Message = "The node-usage record for run {RunId} at seq {Seq} already exists")]
    private static partial void LogAlreadyRecorded(ILogger logger, Guid runId, long seq);

    [LoggerMessage(Level = LogLevel.Error, Message = "The node-usage record for run {RunId} at seq {Seq} was refused: {Error}")]
    private static partial void LogRejected(ILogger logger, Guid runId, long seq, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "The node-usage record for run {RunId} at seq {Seq} could not be written; the next boot's backfill writes it")]
    private static partial void LogAppendFailed(ILogger logger, Exception exception, Guid runId, long seq);
}
