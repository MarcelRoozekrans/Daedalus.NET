using System.Collections.Concurrent;
using Thalos.Memory;

namespace Daedalus.Agents.Memory;

/// <summary>
///     Which <see cref="MemoryRecallTier"/> answered the most recent recall of each live workflow run, held
///     only long enough for that run's next transition to write it into the run record.
/// </summary>
/// <remarks>
///     <b>Why this exists at all.</b> Thalos 0.9.0's <c>MemoryService.RecallAsync</c> reports the tier that
///     answered, and both read paths a turn can take — <c>MemoryContextProvider</c>'s auto-recall and the
///     <c>memory__recall</c> tool — go through it. Neither carries the tier out of the turn:
///     <c>MemoryContextProvider</c> publishes <c>MemoryRecalledEvent</c> without it, and publishes nothing at
///     all when the recall came back empty, which is precisely the case design section 5.4 is about. So the
///     tier exists, is computed correctly, and reaches nothing durable. This is the carrier between the one
///     place that knows it and the one place that can persist it.
///     <para>
///     <b>Last write wins, per run.</b> A node that takes several turns — a <c>review</c> node running three
///     lens passes is the shipped case — recalls once per turn, and the value read back on that node's
///     transition is the tier that answered its <em>last</em> turn. The alternative, a list per node, would
///     have to be bounded and truncated like every other variable, and a truncated tier history is less
///     auditable than one honest value. What the run record therefore claims is "the last recall this node made
///     was answered by tier N", and <c>WorkflowRunModeStore</c> writes it under a key that says so.
///     </para>
///     <para>
///     <b>Entries are removed when read, never expired on a timer.</b> A run that fails mid-turn, or a host
///     that restarts, leaves an entry behind; it is bounded by the number of live runs on one host, and a stale
///     entry can only be read by the same run's own next transition, which is the transition it belongs to
///     anyway. Removing on read is what stops a node that made no recall from silently inheriting the previous
///     node's tier — the failure this whole mechanism exists to make visible, reappearing one level up.
///     </para>
/// </remarks>
internal sealed class WorkflowRecallTierLog
{
    private readonly ConcurrentDictionary<Guid, MemoryRecallTier> _tiers = new();

    /// <summary>Records that <paramref name="tier"/> answered a recall made by a turn of run <paramref name="runId"/>.</summary>
    public void Record(Guid runId, MemoryRecallTier tier) => _tiers[runId] = tier;

    /// <summary>
    ///     Takes and removes the tier recorded for <paramref name="runId"/>, or <see langword="null"/> when no
    ///     turn of that run has recalled since the last time this was read.
    /// </summary>
    public MemoryRecallTier? Take(Guid runId) => _tiers.TryRemove(runId, out var tier) ? tier : null;
}
