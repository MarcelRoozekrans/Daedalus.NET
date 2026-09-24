using Daedalus.Agents.Memory;
using Thalos.Workflow;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     Writes what the run record has to say about <em>how</em> a turn was staffed and what it knew, onto every
///     transition the dispatcher completes: which squad mode the host was in, and which
///     <c>MemoryRecallTier</c> answered that node's last recall.
/// </summary>
/// <remarks>
///     <b>Why a variable on the transition, and not a log line.</b> Design section 7 requires the squad-off
///     fallback to be recorded in <c>workflow_run_event</c>, not only logged, because a log is not part of the
///     run's record and is not what anyone reads six months later. <c>OrmWorkflowStore</c> appends to that
///     table from exactly four places, all private, and the only one a host can put a value into is
///     <c>ApplyTransitionAsync</c>, which writes <see cref="NodeResult.Variables"/> into the event row's
///     <c>variables</c> column <em>unmerged</em> — so each event shows exactly what that one transition
///     contributed. That is the channel this type uses. The same values also merge into
///     <see cref="WorkflowRun.Variables"/>, which is how they stay readable off the run itself and not only by
///     replaying its log.
///     <para>
///     <b>Every transition, not only the first.</b> A host decorator has no cheap, reliable "is this the
///     first" signal — <c>CompleteNodeAsync</c>'s <c>seq</c> starts at 1 only because of a private constant in
///     <c>OrmWorkflowStore</c>. Task B4 does now read the run back on every completion, for <see cref="PinningKey"/>
///     — see that override's own remarks for why that particular round trip is safe — but the underlying choice
///     stands regardless: recording on every transition is more honest than recording once, not only cheaper.
///     A run parked at an approval gate
///     for days can resume onto a host whose <c>Thalos:Squad:Enabled</c> has since changed, and a record that
///     claimed the mode of the first transition would describe a mode half the run never executed in. Each
///     event states the mode that produced <em>that</em> transition.
///     </para>
///     <para>
///     <b>What it does not do.</b> It never removes or rewrites a variable the node itself reported, and it
///     never annotates <c>ResumeAsync</c>: a gate resume is a human's signal, not an agent turn, and
///     <see cref="IWorkflowStore.ResumeAsync"/> takes a payload string rather than a variable bag, so there is
///     nothing to add and no turn to describe. The transition that follows the gate carries the mode again.
///     </para>
///     <para>
///     <b>The keys sit above the cap, not inside it.</b> All three — <see cref="SquadModeKey"/>,
///     <see cref="RecallTierKey"/> and, from task B4, <see cref="PinningKey"/> — are added <em>after</em> every
///     check of a node's report against Thalos' sixteen-key limit — the dispatcher's own, and
///     <see cref="ReviewHandoffWorkflowStore"/>'s re-check on a projected node — so a node can never be failed
///     for a key this type added, and the persisted bag can hold sixteen reported keys plus these three. That is
///     the same shape as <c>IWorkflowStore.ResumeAsync</c>'s payload key, which Thalos' own derivation of
///     <c>WorkflowVariableBlock.MaxOmittedKeyListLength</c> already carries as "plus one for the engine-minted
///     payload"; these are three more of exactly that kind, and the derivation's slack does not account for
///     them. The consequence is bounded and permanent rather than growing, because every one of the three is
///     overwritten rather than accumulated: the omitted-key list can fall three names short of naming every key
///     a rendered block left out, and no further. Growing past that was a separate defect, in the projection
///     rather than here, and is fixed in <see cref="ReviewHandoffWorkflowStore"/>.
///     </para>
///     <para>
///     <b>Task B4 checked, rather than assumed, that a third permanent key is still safe on a review node.</b>
///     The shipped <c>manufacture.yaml</c> carries at most nine distinct keys across a run's whole life —
///     <c>work_intent</c>, <c>files_touched</c>, <c>summary</c>, <c>rationale</c>, <c>learnings</c>,
///     <c>proposed_standing_instructions</c>, plus the three mode keys — nowhere near the cap of sixteen, and
///     <c>review</c>'s own outcome-tool call carries no variables at all (its evidence goes through a separate
///     tool). So <see cref="PinningKey"/> is recorded on every transition, the same as <see cref="SquadModeKey"/>,
///     rather than only on the run's opening event — the fallback the design considered for a process shape that
///     would actually threaten the cap, which this one does not.
///     </para>
/// </remarks>
internal sealed class WorkflowRunModeStore(IWorkflowStore inner, SquadOptions squad, WorkflowRecallTierLog tiers)
    : DelegatingWorkflowStore(inner)
{
    /// <summary>The variable and event key carrying which squad mode produced a transition.</summary>
    public const string SquadModeKey = "squad_mode";

    /// <summary>The variable and event key carrying which recall tier answered the node's last turn.</summary>
    public const string RecallTierKey = "recall_tier";

    /// <summary>The variable and event key carrying whether this run was started with a pinned <see cref="RunManifest"/>.</summary>
    public const string PinningKey = "pinning";

    /// <summary>What <see cref="PinningKey"/> reads when the run carries a <see cref="RunManifest"/>.</summary>
    public const string PinningManifestValue = "manifest";

    /// <summary>What <see cref="PinningKey"/> reads when the run carries none — started before manifests existed, or through the legacy positional <c>StartAsync</c> overload.</summary>
    public const string PinningNoneValue = "none";

    /// <summary>What <see cref="SquadModeKey"/> reads when the roles ran as themselves.</summary>
    public const string SquadEnabledValue = "enabled: each role ran as its own agent";

    /// <summary>
    ///     The prefix <see cref="SquadModeKey"/> reads when every role collapsed onto one agent. The rest of the
    ///     value names that agent.
    /// </summary>
    public const string SquadDisabledPrefix = "disabled";

    private readonly SquadOptions _squad = squad ?? throw new ArgumentNullException(nameof(squad));
    private readonly WorkflowRecallTierLog _tiers = tiers ?? throw new ArgumentNullException(nameof(tiers));

    /// <summary>
    ///     The value written under <see cref="SquadModeKey"/> for the given options — a whole sentence rather
    ///     than a bare flag, because the audience is a human reading <c>workflow_run_event</c> long after the
    ///     configuration that produced it has changed, and "disabled" alone does not say what that cost.
    /// </summary>
    public static string DescribeMode(SquadOptions squad)
    {
        ArgumentNullException.ThrowIfNull(squad);
        return squad.Enabled
            ? SquadEnabledValue
            : $"{SquadDisabledPrefix}: every role ran as agent '{squad.FallbackAgentName}', so one agent implemented and reviewed its own work";
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <see cref="PinningKey"/> needs <see cref="WorkflowRun.Manifest"/>, which this override's parameters do
    ///     not carry — <c>IWorkflowStore.CompleteNodeAsync</c> takes a <c>runId</c>, not a run — so this reads the
    ///     run back through <see cref="DelegatingWorkflowStore.Inner"/> first. That costs one round trip this type
    ///     did not previously make, and is safe to make on every transition because <see cref="WorkflowRun.Manifest"/>
    ///     is write-once: it cannot go stale between this read and the write below the way a live value could.
    /// </remarks>
    public override async ValueTask CompleteNodeAsync(Guid runId, long seq, WorkflowTransition transition, NodeResult result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);

        var run = await Inner.FindAsync(runId, ct).ConfigureAwait(false);
        var annotated = new Dictionary<string, object?>(result.Variables, StringComparer.Ordinal)
        {
            [SquadModeKey] = DescribeMode(_squad),
            [PinningKey] = run?.Manifest is null ? PinningNoneValue : PinningManifestValue,
        };

        // Taken, not peeked: a node whose turn made no recall must not inherit the previous node's tier and
        // report it as its own. Absent means "this node's turn recalled nothing at all", which is a different
        // statement from tier None ("it recalled, and there was nothing in scope") and has to stay tellable
        // apart from it - that distinction is the whole reason the tier is recorded.
        if (_tiers.Take(runId) is { } tier)
        {
            annotated[RecallTierKey] = tier.ToString();
        }

        await Inner.CompleteNodeAsync(runId, seq, transition, new NodeResult(result.Outcome, annotated), ct).ConfigureAwait(false);
    }
}
