using System.Collections.Frozen;
using Thalos.Workflow;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     Narrows a run's variable bag to <see cref="ReviewHandoff.ReviewReads"/> before
///     <see cref="WorkflowNodeDispatcher"/> ever sees it, for a run sitting on a node that runs the review
///     rubric. This is the mechanism that actually withholds the implementer's <c>summary</c> and
///     <c>rationale</c> from the reviewer.
/// </summary>
/// <remarks>
///     <b>Why the projection had to move here, stated plainly because the previous claim was wrong.</b> Task B4
///     built <see cref="ReviewHandoff.ProjectForReview"/> and applied it in <see cref="ReviewLensRunner"/>,
///     which appends the projected keys to the lens pass's task text. Against Thalos 0.8.0 that read as
///     isolation, because nothing populated <see cref="WorkflowRun.Variables"/> at all — the bag was empty on
///     every path, so "the reviewer does not receive the rationale" was true for a reason that had nothing to
///     do with the projection. Thalos 0.9.0 carries a node's reported variables into the bag <em>and</em>
///     renders the whole bag into the next node's task text in <c>WorkflowNodeDispatcher.BuildTaskText</c>. An
///     append-only projection downstream of that withholds nothing: the dispatcher would already have written
///     <c>summary</c> and <c>rationale</c> into the reviewer's prompt before the runner was called.
///     <para>
///     <b>So the withholding is absence, not filtering.</b> The dispatcher is the only component that turns a
///     variable into a model's prompt, and it gets the bag from <see cref="IWorkflowStore.FindAsync"/>. Cutting
///     the keys out there means the values never reach the component that renders them — there is nothing
///     downstream to strip, and no rendering format whose change could silently reopen the leak. Stripping the
///     rendered block back out afterwards would have been exactly that kind of mechanism: correct until
///     someone edits a delimiter.
///     </para>
///     <para>
///     <b>Which nodes are review nodes: the ones that declare <c>lenses:</c>.</b> That is the same declaration
///     <see cref="ReviewLensRunner"/> keys off, read from the same
///     <see cref="IProcessDefinitionStore"/> on the run's own pinned version, so the two cannot disagree about
///     which node is under review. A node with no lenses is untouched and receives the whole bag.
///     </para>
///     <para>
///     <b>Task B4 widens this to a second, narrower node: <c>retrospect</c>.</b> It runs as <c>reviewer</c>, the
///     same withholding role, and for the same reason — it must not see the implementer's <c>summary</c> or
///     <c>rationale</c>, only the durable <see cref="ReviewHandoff.LearningsKey"/>. Retrospect declares no
///     lenses, so it is told apart by its pinned skill instead: <see cref="ProjectionForAsync"/> (renamed from
///     <c>IsReviewNodeAsync</c>, which only ever answered yes/no for one contract) now returns the declared read
///     set for whichever contract applies to the run's current node, or <see langword="null"/> for a node under
///     neither.
///     </para>
///     <para>
///     <b>This narrows what a node is told, never what the run stores.</b> <c>OrmWorkflowStore</c> re-reads
///     the row inside its own transaction before merging, so the persisted bag still holds <c>summary</c> and
///     <c>rationale</c> for humans and for <c>adjudicate</c>, and a human reading a run through
///     <see cref="WorkflowRunGateway"/> sees the full bag because only the dispatcher's copy of the store is
///     decorated.
///     </para>
///     <para>
///     <b>Why <see cref="CompleteNodeAsync"/> is overridden as well: the projection takes Thalos' key cap out
///     of the loop on exactly the node it applies to.</b> <c>WorkflowNodeDispatcher.BuildNodeResult</c> checks
///     a node's report against <c>WorkflowRun.Variables</c>, and on a review node that is the projected
///     two-key bag rather than the real one. The consequence is not an undercount of a few keys, it is that the
///     cap stops binding there at all: <c>review</c> may report the per-turn maximum of eight fresh keys on
///     each of its five permitted visits, and every time the dispatcher computes two plus eight and accepts
///     while the real persisted bag climbs past forty. Thalos derives
///     <c>WorkflowVariableBlock.MaxOmittedKeyListLength</c> from "a bag holds at most
///     <see cref="MaxVariableKeys"/> keys", and says in the same place that the list's own cut "is a backstop
///     that cannot fire while this derivation holds" — so breaking the premise makes that cut reachable, and a
///     reachable cut means <c>implement</c>'s next task text silently omits keys the omission notice does not
///     name. That is the completeness guarantee the cap exists to provide.
///     </para>
///     <para>
///     So the cap is enforced here instead, on the write path, against the bag the run really holds:
///     <see cref="DelegatingWorkflowStore.Inner"/>'s own <c>FindAsync</c> rather than this type's projecting
///     one. A breach fails the run through <c>FailAsync</c> rather than throwing, which is the same choice
///     Thalos makes for a report it
///     rejects and for the same reason — a failed run carries a message a process author can read back out of
///     <c>WorkflowRun.LastError</c>, and a throw here would escape the dispatcher and dead-letter the dispatch
///     instead. The check also counts the three keys <see cref="WorkflowRunModeStore"/> adds below it on every
///     transition, and keeps room for the keys <c>open-pull-request</c> writes, on every agent node rather than only a
///     projected one; see <see cref="DescribeKeyLimitBreach"/>.
///     </para>
/// </remarks>
internal sealed class ReviewHandoffWorkflowStore(IWorkflowStore inner, IProcessDefinitionStore definitions)
    : DelegatingWorkflowStore(inner)
{
    /// <summary>
    ///     The most distinct keys a run's variable bag may hold, mirroring
    ///     <c>Thalos.Workflow.WorkflowVariableBlock.MaxVariableKeys</c>.
    /// </summary>
    /// <remarks>
    ///     Restated rather than referenced because that type is <see langword="internal"/> to
    ///     <c>Thalos.NET.Workflow</c> and no <c>InternalsVisibleTo</c> reaches this assembly. A restated
    ///     constant is a claim about another package's value, so it is guarded like one:
    ///     <c>ReviewHandoffKeyLimitTests</c> reads the real field back out of the loaded assembly by reflection
    ///     and fails if the two ever disagree. Enforcing a <em>different</em> number here would be worse than
    ///     not enforcing one at all — it is the shared bound that makes the omitted-key list provably complete.
    /// </remarks>
    internal const int MaxVariableKeys = 16;

    private readonly IProcessDefinitionStore _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));

    /// <inheritdoc />
    public override async ValueTask<WorkflowRun?> FindAsync(Guid runId, CancellationToken ct)
    {
        var run = await Inner.FindAsync(runId, ct).ConfigureAwait(false);
        if (run is null || run.Variables.Count == 0)
        {
            return run;
        }

        var reads = await ProjectionForAsync(run, ct).ConfigureAwait(false);
        return reads is not null
            ? run with { Variables = ReviewHandoff.Project(run.Variables, reads) }
            : run;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <b>Two jobs, on every transition.</b> The first is <see cref="EnforceAuthorship"/>: each key in
    ///     <see cref="ReviewHandoff.Authors"/> is written only by the node pinned to its author skill, and when
    ///     retrospect completes, <see cref="ReviewHandoff.ProposedStandingInstructionsKey"/> holds exactly what that
    ///     completion proposed, or nothing. That rule has to see every node, because the node that would plant a key
    ///     is any node but its author. So this method reads the run and its definition on every transition, not only
    ///     on a projected one.
    ///     <para>
    ///     The second job is the key-cap check with the host's reservation, on every agent node: see
    ///     <see cref="DescribeKeyLimitBreach"/>. The dispatcher counted a node that is not projected against the full
    ///     cap, but not against the room <see cref="ReviewHandoff.HostActionKeys"/> needs, and a projected node against
    ///     neither. A host action node is not checked here: Thalos checks its report itself, and its keys are the ones
    ///     the reservation holds room for. The check counts the report <em>after</em> the authorship rule has rewritten
    ///     it. That rule only ever removes a key or overwrites one the run already holds, so it can never be the reason
    ///     a report crosses the cap.
    ///     </para>
    /// </remarks>
    public override async ValueTask CompleteNodeAsync(
        Guid runId, long seq, WorkflowTransition transition, NodeResult result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);

        var run = await Inner.FindAsync(runId, ct).ConfigureAwait(false);
        if (run is null)
        {
            await Inner.CompleteNodeAsync(runId, seq, transition, result, ct).ConfigureAwait(false);
            return;
        }

        var node = await NodeForAsync(run, ct).ConfigureAwait(false);
        var authored = EnforceAuthorship(run, node, DropReviewVariables(node, result));

        if (node is { Action: null })
        {
            var breach = DescribeKeyLimitBreach(run, authored.Variables);
            if (breach is not null)
            {
                await Inner.FailAsync(runId, breach, ct).ConfigureAwait(false);
                return;
            }
        }

        await Inner.CompleteNodeAsync(runId, seq, transition, authored, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Enforces <see cref="ReviewHandoff.Authors"/> on the write path: each listed key is written only by the node
    ///     pinned to its author skill. Returns <paramref name="result"/> unchanged when there is nothing to enforce, or a
    ///     copy with the foreign keys removed and, for retrospect, <see cref="ReviewHandoff.ProposedStandingInstructionsKey"/>
    ///     removed or set to <see langword="null"/>.
    /// </summary>
    /// <remarks>
    ///     <b>Why this is needed.</b> Thalos merges each node's reported variables into the run's bag. A later
    ///     write wins, and a report that leaves a key out leaves the earlier value in place. The dispatcher accepts
    ///     any key name a node reports. So without this rule, <c>implement</c> or <c>review</c> could report the
    ///     proposal key, <c>retrospect</c> could then report <c>none</c> with no variables as its skill tells it to,
    ///     and the planted value would reach the gate. There, <c>GET</c> would show it as retrospect's diff and an
    ///     apply would write it to disk. In the same way a review lens could overwrite the implementer's
    ///     <c>summary</c>, which <c>open-pull-request</c> renders into the pull request body as the implementer's own
    ///     account (phase 2.5 task B14).
    ///     <list type="bullet">
    ///         <item>
    ///             <b>A node not pinned to a key's author skill</b> has that key removed from its report. The rest of
    ///             the report is kept.
    ///         </item>
    ///         <item>
    ///             <b>A retrospect completion</b> keeps a reported proposal only when its outcome is
    ///             <see cref="ReviewHandoff.RetrospectProposedOutcome"/>. On any other outcome, or when
    ///             <c>proposed</c> arrives without the key, the key is set to <see langword="null"/> if the report
    ///             or the run's bag holds it. <see cref="StandingInstructionsWriter"/> already treats null as no
    ///             proposal. A null is written only over a key the run already holds, so this never adds a key to the
    ///             bag and never moves it toward the key cap. <c>summary</c> has no such rule: implement's report of
    ///             it is kept as it stands, on either outcome.
    ///         </item>
    ///     </list>
    ///     <para>
    ///     A node is identified the same way <see cref="ProjectionFor"/> identifies it: by the skill it declares on
    ///     the run's own pinned process version, not by the node's name. A node whose definition cannot be resolved
    ///     authors nothing, so every listed key is stripped from its report. The dispatcher fails such a run anyway.
    ///     </para>
    /// </remarks>
    private static NodeResult EnforceAuthorship(WorkflowRun run, ProcessNode? node, NodeResult result)
    {
        var authored = StripForeignKeys(node, result);
        return IsRetrospect(node) ? SettleProposal(run, authored) : authored;
    }

    /// <summary>
    ///     Removes every <see cref="ReviewHandoff.Authors"/> key <paramref name="node"/> is not the author of, and every
    ///     <see cref="ReviewHandoff.HostWritten"/> key unless <paramref name="node"/> is the host action that writes it.
    ///     An agent node is never that action, so no agent can write <c>work_intent</c>, <c>pr_url</c> or
    ///     <c>publish_error</c>.
    /// </summary>
    private static NodeResult StripForeignKeys(ProcessNode? node, NodeResult result)
    {
        Dictionary<string, object?>? copy = null;
        foreach (var (key, author) in ReviewHandoff.Authors)
        {
            if (result.Variables.ContainsKey(key) && !string.Equals(node?.Skill, author, StringComparison.Ordinal))
            {
                copy ??= new Dictionary<string, object?>(result.Variables, StringComparer.Ordinal);
                copy.Remove(key);
            }
        }

        foreach (var (key, action) in ReviewHandoff.HostWritten)
        {
            if (result.Variables.ContainsKey(key) && (action is null || !string.Equals(node?.Action, action, StringComparison.Ordinal)))
            {
                copy ??= new Dictionary<string, object?>(result.Variables, StringComparer.Ordinal);
                copy.Remove(key);
            }
        }

        return copy is null ? result : new NodeResult(result.Outcome, copy) { Usage = result.Usage };
    }

    /// <summary>
    ///     On a retrospect completion, leaves <see cref="ReviewHandoff.ProposedStandingInstructionsKey"/> holding exactly
    ///     what a <c>proposed</c> outcome reported, or <see langword="null"/>.
    /// </summary>
    private static NodeResult SettleProposal(WorkflowRun run, NodeResult result)
    {
        const string key = ReviewHandoff.ProposedStandingInstructionsKey;
        var reported = result.Variables.ContainsKey(key);

        if (reported && string.Equals(result.Outcome, ReviewHandoff.RetrospectProposedOutcome, StringComparison.Ordinal))
        {
            return result;
        }

        if (!reported && !run.Variables.ContainsKey(key))
        {
            return result;
        }

        var cleared = new Dictionary<string, object?>(result.Variables, StringComparer.Ordinal) { [key] = null };
        return new NodeResult(result.Outcome, cleared) { Usage = result.Usage };
    }

    /// <summary>
    ///     On a review node, returns <paramref name="result"/> with the same outcome and usage and an empty variable
    ///     set; on any other node, <paramref name="result"/> itself.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     <b>Why a review node's reported variables are dropped.</b> Nothing reads them. Review evidence lives in the
    ///     host-written review-evidence records, which <see cref="ReviewLensRunner"/> fills from the arguments of
    ///     <c>daedalus__report_review_outcome</c>, not from the bag. Retrospect reads only
    ///     <see cref="ReviewHandoff.RetrospectReads"/> and publish reads implement's <c>summary</c>. A review variable is
    ///     therefore dead data, but it is not harmless: the bag is shared run state under one cap of
    ///     <see cref="MaxVariableKeys"/> keys, so a reviewer that reports half a dozen free-form keys can fail the run
    ///     at <see cref="DescribeKeyLimitBreach"/> for data no one will ever read. Whether a run survives its review
    ///     would then depend on how chatty the model was (live run e973b5e7 failed on a reviewer's six).
    ///     </para>
    ///     <para>
    ///     <b>Absence, not filtering</b>, as for the read projection: the variables never reach the cap check or the
    ///     inner store, so there is no downstream copy to strip. The outcome and usage pass through unchanged. The
    ///     empty set is a real dictionary because <see cref="NodeResult"/> carries a non-null one.
    ///     </para>
    ///     <para>
    ///     A future consumer of a review variable needs an explicit contract (a named key in
    ///     <see cref="ReviewHandoff.Authors"/> style, with its author and reader declared). It does not get one by
    ///     being reported.
    ///     </para>
    /// </remarks>
    private static NodeResult DropReviewVariables(ProcessNode? node, NodeResult result) =>
        IsReviewNode(node) && result.Variables.Count > 0
            ? new NodeResult(result.Outcome, new Dictionary<string, object?>(StringComparer.Ordinal)) { Usage = result.Usage }
            : result;

    /// <summary>A review node is one that declares <c>lenses:</c>, the declaration <see cref="ReviewLensRunner"/> keys off.</summary>
    private static bool IsReviewNode(ProcessNode? node) => node is { Lenses.Count: > 0 };

    private static bool IsRetrospect(ProcessNode? node) =>
        node is not null && string.Equals(node.Skill, ReviewHandoff.RetrospectSkillName, StringComparison.Ordinal);

    /// <summary>
    ///     The keys <see cref="WorkflowRunModeStore"/> adds to every transition below this store. Counted by
    ///     <see cref="DescribeKeyLimitBreach"/> as if already in the bag, so the first transition, before they are, is
    ///     held to the same room as every later one.
    /// </summary>
    private static readonly string[] ModeKeys =
        [WorkflowRunModeStore.SquadModeKey, WorkflowRunModeStore.PinningKey, WorkflowRunModeStore.RecallTierKey];

    /// <summary>
    ///     The failure message for an agent node's report that mints a key and would take the run's real bag, together
    ///     with the <see cref="ModeKeys"/> and the <see cref="ReviewHandoff.HostActionKeys"/> the host still has to
    ///     write, past <see cref="MaxVariableKeys"/> distinct keys; or <see langword="null"/> when it would not.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     Distinct keys after the merge, so overwriting a key the run already holds never moves the count and
    ///     a capped loop can overwrite the same few keys lap after lap, the same rule Thalos applies. A report that
    ///     mints no new key is never refused, even over a bag already past the limit.
    ///     </para>
    ///     <para>
    ///     <b>Counted against the real bag.</b> A review or retrospect dispatch is narrowed to a declared read
    ///     projection, so the bag the dispatcher counted that node against is not what the run holds.
    ///     </para>
    ///     <para>
    ///     <b>Room kept for the host.</b> <c>open-pull-request</c> writes <c>pr_url</c> or <c>publish_error</c> after
    ///     it has pushed and opened the pull request, and Thalos checks those keys against the cap only then. A bag
    ///     agents had filled would fail the run after its side effects, so no agent node may take that room.
    ///     </para>
    /// </remarks>
    private static string? DescribeKeyLimitBreach(WorkflowRun run, IReadOnlyDictionary<string, object?> reported)
    {
        if (reported.Keys.All(run.Variables.ContainsKey))
        {
            return null;
        }

        var merged = new HashSet<string>(run.Variables.Keys, StringComparer.Ordinal);
        merged.UnionWith(reported.Keys);
        var reachedByReport = merged.Count;
        merged.UnionWith(ModeKeys);
        merged.UnionWith(ReviewHandoff.HostActionKeys);
        if (merged.Count <= MaxVariableKeys)
        {
            return null;
        }

        return $"Node '{run.CurrentNode}' reported {reported.Count} variable(s), which would take the run's variable bag to " +
            $"{reachedByReport} distinct keys, and to {merged.Count} with the keys the host adds on every transition " +
            $"({string.Join(", ", ModeKeys)}) and the room it keeps for {ReviewHandoff.PublishActionName} " +
            $"({string.Join(", ", ReviewHandoff.HostActionKeys)}): past the limit of {MaxVariableKeys}. Counted against " +
            $"the run's real bag of {run.Variables.Count} key(s). Overwrite an existing key instead of minting a new one, " +
            "or report fewer, larger-grained variables.";
    }

    /// <summary>
    ///     The declared read set the node the run is currently on is projected down to, or <see langword="null"/>
    ///     when the node carries no projection at all and is given the whole bag. A review node (one that
    ///     declares <c>lenses:</c>) yields <see cref="ReviewHandoff.ReviewReads"/>; a <c>retrospect</c> node (one
    ///     pinned to <see cref="ReviewHandoff.RetrospectSkillName"/>) yields <see cref="ReviewHandoff.RetrospectReads"/>.
    ///     A definition or node that cannot be resolved answers <see langword="null"/>: the dispatcher resolves
    ///     both itself a moment later and fails the run with its own message, and a run that is about to fail has
    ///     no reviewer or retrospect step to isolate.
    /// </summary>
    private async ValueTask<FrozenSet<string>?> ProjectionForAsync(WorkflowRun run, CancellationToken ct) =>
        ProjectionFor(await NodeForAsync(run, ct).ConfigureAwait(false));

    /// <summary>The projection for <paramref name="node"/>; see <see cref="ProjectionForAsync"/>.</summary>
    private static FrozenSet<string>? ProjectionFor(ProcessNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (IsReviewNode(node))
        {
            return ReviewHandoff.ReviewReads;
        }

        return IsRetrospect(node) ? ReviewHandoff.RetrospectReads : null;
    }

    /// <summary>
    ///     The node the run is currently on, read from the run's own pinned process version, or
    ///     <see langword="null"/> when the definition or the node cannot be resolved.
    /// </summary>
    private async ValueTask<ProcessNode?> NodeForAsync(WorkflowRun run, CancellationToken ct)
    {
        var definition = await _definitions.GetAsync(run.Process, run.ProcessVersion, ct).ConfigureAwait(false);
        return definition.IsSuccess && definition.Value.Nodes.TryGetValue(run.CurrentNode, out var node)
            ? node
            : null;
    }
}
