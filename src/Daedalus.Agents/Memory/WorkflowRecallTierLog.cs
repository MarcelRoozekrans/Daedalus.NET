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
///     <b>Entries are removed when read, never expired on a timer.</b> Removing on read is what stops a node
///     that made no recall from silently inheriting the previous node's tier — the failure this whole
///     mechanism exists to make visible, reappearing one level up.
///     </para>
///     <para>
///     <b>What that actually bounds, corrected.</b> This was described as "bounded by the number of live runs
///     on one host", which would be true if every entry were eventually read. <see cref="Take"/> is called
///     from <c>WorkflowRunModeStore.CompleteNodeAsync</c> and nowhere else, and <c>FailAsync</c> is not
///     decorated — so a run whose turn recalled and then failed the node leaves its entry behind permanently,
///     as does a run stranded by a dead-lettered dispatch. The real bound is the number of runs that have
///     failed or stranded mid-node since this process started, and the reclaim is a host restart. That is
///     acceptable at the size of the value — one enum per failed run — and it is not what the earlier wording
///     claimed. A stale entry is still harmless to correctness: it is keyed by run id, so only that run's own
///     next transition could read it, and a failed run has none.
///     </para>
/// </remarks>
internal sealed class WorkflowRecallTierLog
{
    private readonly ConcurrentDictionary<Guid, MemoryRecallTier> _tiers = new();

    /// <summary>
    ///     Whether nothing at all is currently recorded. Exists so "this recall was attributed to no run" is
    ///     directly assertable: probing <see cref="Take"/> with a run id can only show that one id is absent,
    ///     which a recording keyed on some other default id would satisfy just as well.
    /// </summary>
    public bool IsEmpty => _tiers.IsEmpty;

    /// <summary>Records that <paramref name="tier"/> answered a recall made by a turn of run <paramref name="runId"/>.</summary>
    public void Record(Guid runId, MemoryRecallTier tier) => _tiers[runId] = tier;

    /// <summary>
    ///     Takes and removes the tier recorded for <paramref name="runId"/>, or <see langword="null"/> when no
    ///     turn of that run has recalled since the last time this was read.
    /// </summary>
    public MemoryRecallTier? Take(Guid runId) => _tiers.TryRemove(runId, out var tier) ? tier : null;
}
