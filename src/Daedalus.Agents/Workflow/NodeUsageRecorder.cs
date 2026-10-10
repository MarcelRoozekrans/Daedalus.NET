using Daedalus.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Thalos;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     Appends one <see cref="WorkflowRunRecord.NodeUsageKind"/> record for a completed agent node, so cost analytics
///     reads a manufacture run's usage from Daedalus's own table. <see cref="ReviewHandoffWorkflowStore"/> calls it on
///     every completion, and <c>NodeUsageBackfill</c> calls it for completions that have no record yet.
/// </summary>
/// <remarks>
///     A failure is logged, not thrown. The completion it describes is already persisted, so throwing would only
///     dead-letter a dispatch whose work is done. The next boot's backfill writes the missing record.
/// </remarks>
internal sealed partial class NodeUsageRecorder(IServiceScopeFactory scopes, TimeProvider clock, ILogger<NodeUsageRecorder> logger)
{
    /// <summary>The principal of every node-usage record: the host wrote it, no caller did.</summary>
    public const string HostPrincipalId = "host";

    /// <summary>Now, on the host clock, as a UTC <see cref="DateTime"/>.</summary>
    public DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    /// <summary>Appends the record. Returns whether it was written.</summary>
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
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IWorkflowRunRecordStore>()
                .AppendAsync(record.Value, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogAppendFailed(logger, ex, runId, seq);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The node-usage record for run {RunId} at seq {Seq} was refused: {Error}")]
    private static partial void LogRejected(ILogger logger, Guid runId, long seq, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "The node-usage record for run {RunId} at seq {Seq} could not be written; the next boot's backfill writes it")]
    private static partial void LogAppendFailed(ILogger logger, Exception exception, Guid runId, long seq);
}
