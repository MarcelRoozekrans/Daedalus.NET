using System.Collections.Concurrent;
using Daedalus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Thalos;
using Thalos.Workflow;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     Appends one <see cref="WorkflowRunRecord.NodeUsageKind"/> record for a completed agent node, so cost analytics
///     reads a manufacture run's usage from Daedalus's own table. <see cref="ReviewHandoffWorkflowStore"/> calls
///     <see cref="RecordCompletionAsync"/> on every completion, which also counts the tokens.
///     <see cref="NodeUsageBackfill"/> calls <see cref="RecordAsync"/> for completions that have no record yet, which counts
///     nothing: those tokens were spent in the past. Since phase 2.15 <see cref="FailedNodeUsageRunner"/> hands the turns of a
///     node whose turn failed, which no completion records, to <see cref="HoldFailure"/>, and the record is appended once the
///     store has committed the run's failure.
/// </summary>
/// <remarks>
///     A second append for a (run, seq) that already has a record violates the unique index
///     <see cref="WorkflowRunRecord.NodeUsageIndexName"/>. That is the expected outcome of the live append racing the
///     backfill, so it reads as "already recorded": it returns false and logs at Debug.
///     A failure is logged, not thrown, and so is a cancelled caller: the append runs under a token of its own, which only
///     <c>AppendTimeout</c> cancels, never the dispatch's. The completion it describes is already persisted, so throwing would only
///     dead-letter a dispatch whose work is done. The next boot's backfill writes the missing record of a completion; a failed
///     node's record has no completion to be rebuilt from, so its failed append is logged as lost spend.
///     <para>
///     <b>A node that ran on several models is recorded per model.</b> Thalos hands the store one <see cref="TurnUsage"/> per
///     completion, and its <c>+</c> keeps the first operand's model, so a review whose pinned lens ran on another model would be
///     priced wholly at the first pass's model. <see cref="ReviewLensRunner"/> therefore hands its passes' usage to
///     <see cref="HoldPasses"/> before it returns, keyed by the run and the seq the completion will carry, and
///     <see cref="RecordCompletionAsync"/> takes it back: the record carries <see cref="NodeUsage.ByModel"/> and the token
///     counter counts each model's own tokens. What is held is in memory and released whenever no completion will take it:
///     when the dispatcher's store fails the run or its completion throws, and, as an <see cref="IWorkflowRunObserver"/>,
///     whenever the store commits a cancellation or a failure of the run by any path. A host restart loses it. The backfill
///     reads only the completion's single usage, so a review it records is priced at its first model, as before this existed.
///     </para>
///     <para>
///     <b>A failed node is recorded only once its run's failure has committed.</b> <see cref="HoldFailure"/> keeps the summed
///     usage per (run, seq) apart from the passes above, and <see cref="OnChangedAsync"/> appends it when the store commits
///     the run's failure, which Thalos' <c>OrmWorkflowStore</c> notifies only after <c>FailAsync</c> or
///     <c>FailStrandedAsync</c> has committed. A failure that never commits, such as a graceful stop's
///     <c>FailAsync</c> under the cancelled dispatch token or a transient database error inside it, is redelivered: the node
///     runs again at the same seq, <see cref="DropFailure"/> releases the hold before it does, and that run's own completion
///     or failure is what is recorded. The cancelled attempt's partial spend is then not recorded, which keeps every seq
///     counted once. A committed cancellation releases the hold too. A host restart loses it, as it loses the passes.
///     </para>
/// </remarks>
internal sealed partial class NodeUsageRecorder(IServiceScopeFactory scopes, TimeProvider clock, ILogger<NodeUsageRecorder> logger)
    : IWorkflowRunObserver
{
    /// <summary>The principal of every node-usage record: the host wrote it, no caller did.</summary>
    public const string HostPrincipalId = "host";

    /// <summary>How long one append may take, under a token of its own.</summary>
    private static readonly TimeSpan AppendTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The per-model usage of a node that ran on more than one model, until its completion is recorded.</summary>
    private readonly ConcurrentDictionary<(Guid RunId, long Seq), IReadOnlyList<NodeUsage>> _held = new();

    /// <summary>The usage of a node whose turn failed, until the store commits the run's failure.</summary>
    private readonly ConcurrentDictionary<(Guid RunId, long Seq), FailedNode> _failed = new();

    /// <summary>Now, on the host clock, as a UTC <see cref="DateTime"/>.</summary>
    public DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    /// <summary>How many nodes' per-model usage is held, for tests.</summary>
    internal int HeldCount => _held.Count;

    /// <summary>How many failed nodes' usage is held, for tests.</summary>
    internal int HeldFailureCount => _failed.Count;

    /// <summary>
    ///     Holds the usage of each turn a node at (<paramref name="runId"/>, <paramref name="seq"/>) ran, summed per model in
    ///     the order the models first ran, until <see cref="RecordCompletionAsync"/> records that completion. Turns that all
    ///     ran on one model hold nothing, and clear what an earlier delivery of the same node held: that node is recorded as
    ///     one model, as it always was.
    /// </summary>
    public void HoldPasses(Guid runId, long seq, IReadOnlyList<TurnUsage> passes)
    {
        ArgumentNullException.ThrowIfNull(passes);
        var parts = PerModel(passes);
        if (parts.Count < 2)
        {
            _held.TryRemove((runId, seq), out _);
            return;
        }

        _held[(runId, seq)] = parts;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     A cancelled or failed run completes no node, so the passes it held are dropped. A committed failure appends the
    ///     record of each failed node held for the run, through <see cref="RecordFailureAsync"/>; a committed cancellation
    ///     drops them. Changes no run, and throws nothing: the append logs its own failure.
    /// </remarks>
    public async ValueTask OnChangedAsync(WorkflowRunChange change, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.Kind is not (WorkflowRunChangeKind.Cancelled or WorkflowRunChangeKind.Failed))
        {
            return;
        }

        Forget(change.RunId);
        foreach (var key in _failed.Keys)
        {
            if (key.RunId != change.RunId || !_failed.TryRemove(key, out var failed))
            {
                continue;
            }

            if (change.Kind == WorkflowRunChangeKind.Failed)
            {
                await RecordFailureAsync(key.RunId, key.Seq, failed).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    ///     Drops the passes held for <paramref name="runId"/>: no completion will take them. A failed node's held usage is
    ///     not dropped here; it waits for the store's verdict on the failure, in <see cref="OnChangedAsync"/>.
    /// </summary>
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

        return AppendAsync(runId, seq, node, startedById, total, UtcNow, repairedByBackfill: true);
    }

    /// <summary>
    ///     Phase 2.15, for a node whose turn failed, which no completion records: holds the sum of <paramref name="turns"/>,
    ///     every turn the node ran up to and including the failed one, for (<paramref name="runId"/>, <paramref name="seq"/>)
    ///     until the store commits the run's failure, when <see cref="OnChangedAsync"/> records it. Turns on two or more models
    ///     are held with the per-model split <see cref="HoldPasses"/> builds. <see cref="FailedNodeUsageRunner"/> calls it before
    ///     the dispatcher fails the run, at the seq the node ran at. Returns whether anything is held: false, holding nothing
    ///     and releasing an earlier hold of that seq, when the turns spent no tokens.
    /// </summary>
    public bool HoldFailure(Guid runId, long seq, string node, string? startedById, IReadOnlyList<TurnUsage> turns)
    {
        ArgumentNullException.ThrowIfNull(turns);
        var parts = PerModel(turns);
        var total = new NodeUsage(
            parts.Count == 0 ? null : parts[0].Model,
            parts.Sum(p => p.InputTokens), parts.Sum(p => p.OutputTokens),
            parts.Sum(p => p.CacheReadTokens), parts.Sum(p => p.CacheWriteTokens));
        if (total is { InputTokens: 0, OutputTokens: 0, CacheReadTokens: 0, CacheWriteTokens: 0 })
        {
            DropFailure(runId, seq);
            return false;
        }

        if (parts.Count >= 2)
        {
            // Sums of the same parts, so the split always holds.
            total = total.WithSplit(parts).Value;
        }

        _failed[(runId, seq)] = new FailedNode(node, startedById, total);
        return true;
    }

    /// <summary>
    ///     Releases the failed node held for (<paramref name="runId"/>, <paramref name="seq"/>). <see cref="FailedNodeUsageRunner"/>
    ///     calls it whenever a node is dispatched: a dispatch of a seq that still holds a failure is a redelivery, so the
    ///     earlier failure never committed and the node is about to run again.
    /// </summary>
    public void DropFailure(Guid runId, long seq) => _failed.TryRemove((runId, seq), out _);

    /// <summary>
    ///     Records a held failed node once its run's failure has committed: counts the tokens on
    ///     <c>daedalus.manufacture.tokens</c> per model and appends the node's record as <see cref="RecordCompletionAsync"/>
    ///     does, stamped now. No completion claims that seq, because the run has failed; a second record for it reads as
    ///     "already recorded" through the unique index. Returns whether the record was written.
    /// </summary>
    private ValueTask<bool> RecordFailureAsync(Guid runId, long seq, FailedNode failed)
    {
        foreach (var part in failed.Usage.Parts)
        {
            ManufactureTelemetry.RecordTokens(failed.Node, part);
        }

        LogFailedNodeRecorded(logger, runId, failed.Node, failed.Usage.InputTokens, failed.Usage.OutputTokens);
        return AppendAsync(runId, seq, failed.Node, failed.StartedById, failed.Usage, UtcNow, repairedByBackfill: false);
    }

    /// <summary>Appends the record. Returns whether it was written: false when the record already existed or the append failed. The caller's token is deliberately not passed to the append.</summary>
    public ValueTask<bool> RecordAsync(
        Guid runId, long seq, string node, string? startedById, TurnUsage usage, DateTime createdAtUtc, CancellationToken ct) =>
        AppendAsync(runId, seq, node, startedById, UsageOf(usage), createdAtUtc, repairedByBackfill: true);

    /// <summary>
    ///     The turns summed per model, in the order the models first ran. A turn with no model is grouped under
    ///     <see langword="null"/>.
    /// </summary>
    private static List<NodeUsage> PerModel(IReadOnlyList<TurnUsage> turns) => turns
        .GroupBy(p => string.IsNullOrEmpty(p.ModelId) ? null : p.ModelId, StringComparer.Ordinal)
        .Select(g => new NodeUsage(
            g.Key, g.Sum(p => (long)p.InputTokens), g.Sum(p => (long)p.OutputTokens),
            g.Sum(p => (long)p.CacheReadTokens), g.Sum(p => (long)p.CacheWriteTokens)))
        .ToList();

    private static NodeUsage UsageOf(TurnUsage usage) => new(
        string.IsNullOrEmpty(usage.ModelId) ? null : usage.ModelId,
        usage.InputTokens, usage.OutputTokens, usage.CacheReadTokens, usage.CacheWriteTokens);

    /// <summary>
    ///     Appends <paramref name="usage"/> as the node-usage record of (<paramref name="runId"/>, <paramref name="seq"/>).
    ///     <paramref name="repairedByBackfill"/> says whether the next boot's backfill writes the record when this append
    ///     fails: it does for a completion, never for a failed node, which has no completion to read back.
    /// </summary>
    private async ValueTask<bool> AppendAsync(
        Guid runId, long seq, string node, string? startedById, NodeUsage usage, DateTime createdAtUtc, bool repairedByBackfill)
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
            if (repairedByBackfill)
            {
                LogAppendFailed(logger, ex, runId, seq);
            }
            else
            {
                LogFailedNodeAppendFailed(logger, ex, runId, seq);
            }

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

    [LoggerMessage(Level = LogLevel.Error, Message = "The node-usage record of the failed node of run {RunId} at seq {Seq} could not be written; its spend is not in cost analytics")]
    private static partial void LogFailedNodeAppendFailed(ILogger logger, Exception exception, Guid runId, long seq);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recording the spend of failed node {Node} of run {RunId}: {InputTokens} input and {OutputTokens} output tokens")]
    private static partial void LogFailedNodeRecorded(ILogger logger, Guid runId, string node, long inputTokens, long outputTokens);

    /// <summary>A failed node's record, held until its run's failure commits.</summary>
    private sealed record FailedNode(string Node, string? StartedById, NodeUsage Usage);
}
