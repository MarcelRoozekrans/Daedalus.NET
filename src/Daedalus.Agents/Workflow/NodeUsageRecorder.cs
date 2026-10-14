using System.Collections.Concurrent;
using Daedalus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Thalos;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     Appends one <see cref="WorkflowRunRecord.NodeUsageKind"/> record for a completed agent node, so cost analytics
///     reads a manufacture run's usage from Daedalus's own table. <see cref="ReviewHandoffWorkflowStore"/> calls
///     <see cref="RecordCompletionAsync"/> on every completion, which also counts the tokens.
///     <see cref="NodeUsageBackfill"/> calls <see cref="RecordAsync"/> for completions that have no record yet, which counts
///     nothing: those tokens were spent in the past.
/// </summary>
/// <remarks>
///     A second append for a (run, seq) that already has a record violates the unique index
///     <see cref="WorkflowRunRecord.NodeUsageIndexName"/>. That is the expected outcome of the live append racing the
///     backfill, so it reads as "already recorded": it returns false and logs at Debug.
///     A failure is logged, not thrown, and so is a cancelled caller: the append runs under a token of its own, which only
///     <c>AppendTimeout</c> cancels, never the dispatch's. The completion it describes is already persisted, so throwing would only
///     dead-letter a dispatch whose work is done. The next boot's backfill writes the missing record.
///     <para>
///     <b>A node that ran on several models is recorded per model.</b> Thalos hands the store one <see cref="TurnUsage"/> per
///     completion, and its <c>+</c> keeps the first operand's model, so a review whose pinned lens ran on another model would be
///     priced wholly at the first pass's model. <see cref="ReviewLensRunner"/> therefore hands its passes' usage to
///     <see cref="HoldPasses"/> before it returns, keyed by the run and the seq the completion will carry, and
///     <see cref="RecordCompletionAsync"/> takes it back: the record carries <see cref="NodeUsage.ByModel"/> and the token
///     counter counts each model's own tokens. What is held is in memory: a node whose completion never comes, because the
///     node failed, is forgotten by <see cref="Forget"/> when the dispatcher's store fails the run, and a host restart loses
///     it. The backfill reads only the completion's single usage, so a review it records is priced at its first model, as
///     before this existed.
///     </para>
/// </remarks>
internal sealed partial class NodeUsageRecorder(IServiceScopeFactory scopes, TimeProvider clock, ILogger<NodeUsageRecorder> logger)
{
    /// <summary>The principal of every node-usage record: the host wrote it, no caller did.</summary>
    public const string HostPrincipalId = "host";

    /// <summary>How long one append may take, under a token of its own.</summary>
    private static readonly TimeSpan AppendTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The per-model usage of a node that ran on more than one model, until its completion is recorded.</summary>
    private readonly ConcurrentDictionary<(Guid RunId, long Seq), IReadOnlyList<NodeUsage>> _held = new();

    /// <summary>Now, on the host clock, as a UTC <see cref="DateTime"/>.</summary>
    public DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    /// <summary>How many nodes' per-model usage is held, for tests.</summary>
    internal int HeldCount => _held.Count;

    /// <summary>
    ///     Holds the usage of each turn a node at (<paramref name="runId"/>, <paramref name="seq"/>) ran, summed per model in
    ///     the order the models first ran, until <see cref="RecordCompletionAsync"/> records that completion. Turns that all
    ///     ran on one model hold nothing, and clear what an earlier delivery of the same node held: that node is recorded as
    ///     one model, as it always was.
    /// </summary>
    public void HoldPasses(Guid runId, long seq, IReadOnlyList<TurnUsage> passes)
    {
        ArgumentNullException.ThrowIfNull(passes);
        var parts = passes
            .GroupBy(p => string.IsNullOrEmpty(p.ModelId) ? null : p.ModelId, StringComparer.Ordinal)
            .Select(g => new NodeUsage(
                g.Key, g.Sum(p => (long)p.InputTokens), g.Sum(p => (long)p.OutputTokens),
                g.Sum(p => (long)p.CacheReadTokens), g.Sum(p => (long)p.CacheWriteTokens)))
            .ToList();
        if (parts.Count < 2)
        {
            _held.TryRemove((runId, seq), out _);
            return;
        }

        _held[(runId, seq)] = parts;
    }

    /// <summary>Drops whatever is held for <paramref name="runId"/>: its node failed, so no completion will take it.</summary>
    public void Forget(Guid runId)
    {
        foreach (var key in _held.Keys)
        {
            if (key.RunId == runId)
            {
                _held.TryRemove(key, out _);
            }
        }
    }

    /// <summary>
    ///     The live path, for a completion just persisted: counts its tokens on <c>daedalus.manufacture.tokens</c>, then
    ///     appends its record as <see cref="RecordAsync"/> does, stamped now. It runs at most once per (run, seq): the
    ///     caller calls it only after the store committed this seq, the store refuses a second completion of the same seq,
    ///     and a redelivered dispatch is skipped before its node runs. A crash between the commit and this call loses the
    ///     count, never doubles it. The count does not depend on the append: a failed append is repaired by the next
    ///     boot's backfill, which counts nothing. Returns whether the record was written.
    /// </summary>
    public ValueTask<bool> RecordCompletionAsync(Guid runId, long seq, string node, string? startedById, TurnUsage usage, CancellationToken ct)
    {
        var total = UsageOf(usage);
        if (_held.TryRemove((runId, seq), out var parts))
        {
            var split = total.WithSplit(parts);
            if (split.IsSuccess)
            {
                total = split.Value;
            }
            else
            {
                // A split that does not sum to the completion's usage belongs to some other delivery: the node is recorded
                // as the completion reports it, which is never more wrong than before the split existed.
                LogSplitDiscarded(logger, runId, seq);
            }
        }

        foreach (var part in total.Parts)
        {
            ManufactureTelemetry.RecordTokens(node, part);
        }

        return AppendAsync(runId, seq, node, startedById, total, UtcNow);
    }

    /// <summary>Appends the record. Returns whether it was written: false when the record already existed or the append failed. The caller's token is deliberately not passed to the append.</summary>
    public ValueTask<bool> RecordAsync(
        Guid runId, long seq, string node, string? startedById, TurnUsage usage, DateTime createdAtUtc, CancellationToken ct) =>
        AppendAsync(runId, seq, node, startedById, UsageOf(usage), createdAtUtc);

    private static NodeUsage UsageOf(TurnUsage usage) => new(
        string.IsNullOrEmpty(usage.ModelId) ? null : usage.ModelId,
        usage.InputTokens, usage.OutputTokens, usage.CacheReadTokens, usage.CacheWriteTokens);

    private async ValueTask<bool> AppendAsync(Guid runId, long seq, string node, string? startedById, NodeUsage usage, DateTime createdAtUtc)
    {
        var payload = usage.ToPayloadJson();
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "The per-model usage held for run {RunId} at seq {Seq} does not sum to its completion's usage; the node is recorded as one model")]
    private static partial void LogSplitDiscarded(ILogger logger, Guid runId, long seq);

    [LoggerMessage(Level = LogLevel.Debug, Message = "The node-usage record for run {RunId} at seq {Seq} already exists")]
    private static partial void LogAlreadyRecorded(ILogger logger, Guid runId, long seq);

    [LoggerMessage(Level = LogLevel.Error, Message = "The node-usage record for run {RunId} at seq {Seq} was refused: {Error}")]
    private static partial void LogRejected(ILogger logger, Guid runId, long seq, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "The node-usage record for run {RunId} at seq {Seq} could not be written; the next boot's backfill writes it")]
    private static partial void LogAppendFailed(ILogger logger, Exception exception, Guid runId, long seq);
}
