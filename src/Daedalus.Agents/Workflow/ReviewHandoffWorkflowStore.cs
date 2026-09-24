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
///     instead. What this check does <em>not</em> count is the three keys <see cref="WorkflowRunModeStore"/>
///     is about to add below it, on this same transition; see that type for why they escape only this one
///     check, once, and count against the cap like any other key on every transition after — exactly as
///     <c>IWorkflowStore.ResumeAsync</c>'s engine-minted payload key already does in Thalos' own derivation.
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
    ///     <b>Two jobs, on every transition.</b> The first is <see cref="EnforceProposalAuthorship"/>: only a node
    ///     pinned to <see cref="ReviewHandoff.RetrospectSkillName"/> may write
    ///     <see cref="ReviewHandoff.ProposedStandingInstructionsKey"/>, and when it completes, the key holds exactly
    ///     what that completion proposed, or nothing. That rule has to see every node, because the node that would
    ///     plant the key is any node but retrospect. So this method now reads the run and its definition on every
    ///     transition, not only on a projected one.
    ///     <para>
    ///     The second job is the key-cap re-check, and it still runs only on a projected node. Every other node was
    ///     already counted by the dispatcher against the same bag this would read, so a second check there would be
    ///     a second place for the same rule to live. The check counts the report <em>after</em> the authorship rule
    ///     has rewritten it. That rule only ever removes a key or overwrites one the run already holds, so it can
    ///     never be the reason a report crosses the cap.
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
        var authored = EnforceProposalAuthorship(run, node, result);

        if (ProjectionFor(node) is not null)
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
    ///     Enforces design decision D4 on the write path: the <c>retrospect</c> node is the only author of a
    ///     standing-instructions proposal. Returns <paramref name="result"/> unchanged when there is nothing to
    ///     enforce, or a copy with <see cref="ReviewHandoff.ProposedStandingInstructionsKey"/> removed or set to
    ///     <see langword="null"/>.
    /// </summary>
    /// <remarks>
    ///     <b>Why this is needed.</b> Thalos merges each node's reported variables into the run's bag. A later
    ///     write wins, and a report that leaves a key out leaves the earlier value in place. The dispatcher accepts
    ///     any key name a node reports. So without this rule, <c>implement</c> or <c>review</c> could report the
    ///     key, <c>retrospect</c> could then report <c>none</c> with no variables as its skill tells it to, and the
    ///     planted value would reach the gate. There, <c>GET</c> would show it as retrospect's diff and an apply
    ///     would write it to disk.
    ///     <list type="bullet">
    ///         <item>
    ///             <b>Any node that is not retrospect</b> has the key removed from its report. The rest of the
    ///             report is kept.
    ///         </item>
    ///         <item>
    ///             <b>A retrospect completion</b> keeps a reported key only when its outcome is
    ///             <see cref="ReviewHandoff.RetrospectProposedOutcome"/>. On any other outcome, or when
    ///             <c>proposed</c> arrives without the key, the key is set to <see langword="null"/> if the report
    ///             or the run's bag holds it. <see cref="StandingInstructionsWriter"/> already treats null as no
    ///             proposal. A null is written only over a key the run already holds, so this never adds a key to the
    ///             bag and never moves it toward the key cap.
    ///         </item>
    ///     </list>
    ///     <para>
    ///     Retrospect is identified the same way <see cref="ProjectionFor"/> identifies it: by the skill its node
    ///     declares on the run's own pinned process version, not by the node's name. A node whose definition
    ///     cannot be resolved is treated as not being retrospect, so its report is stripped. The dispatcher fails
    ///     such a run anyway.
    ///     </para>
    /// </remarks>
    private static NodeResult EnforceProposalAuthorship(WorkflowRun run, ProcessNode? node, NodeResult result)
    {
        const string key = ReviewHandoff.ProposedStandingInstructionsKey;
        var reported = result.Variables.ContainsKey(key);

        if (!IsRetrospect(node))
        {
            return reported
                ? new NodeResult(result.Outcome, Without(result.Variables, key))
                : result;
        }

        if (reported && string.Equals(result.Outcome, ReviewHandoff.RetrospectProposedOutcome, StringComparison.Ordinal))
        {
            return result;
        }

        if (!reported && !run.Variables.ContainsKey(key))
        {
            return result;
        }

        var cleared = new Dictionary<string, object?>(result.Variables, StringComparer.Ordinal) { [key] = null };
        return new NodeResult(result.Outcome, cleared);
    }

    private static Dictionary<string, object?> Without(IReadOnlyDictionary<string, object?> variables, string key)
    {
        var copy = new Dictionary<string, object?>(variables, StringComparer.Ordinal);
        copy.Remove(key);
        return copy;
    }

    private static bool IsRetrospect(ProcessNode? node) =>
        node is not null && string.Equals(node.Skill, ReviewHandoff.RetrospectSkillName, StringComparison.Ordinal);

    /// <summary>
    ///     The failure message for a report that would take the run's real bag past
    ///     <see cref="MaxVariableKeys"/> distinct keys, or <see langword="null"/> when it would not.
    /// </summary>
    /// <remarks>
    ///     Distinct keys after the merge, so overwriting a key the run already holds never moves the count and
    ///     a capped loop can overwrite the same few keys lap after lap — the same rule Thalos applies, stated
    ///     the same way, because a review node that obeyed a different one would be a worse surprise than one
    ///     that obeyed none.
    /// </remarks>
    private static string? DescribeKeyLimitBreach(WorkflowRun run, IReadOnlyDictionary<string, object?> reported)
    {
        if (reported.Count == 0)
        {
            return null;
        }

        var merged = new HashSet<string>(run.Variables.Keys, StringComparer.Ordinal);
        merged.UnionWith(reported.Keys);
        if (merged.Count <= MaxVariableKeys)
        {
            return null;
        }

        return $"Node '{run.CurrentNode}' reported {reported.Count} variable(s), which would take the run's variable bag to " +
            $"{merged.Count} distinct keys, past the limit of {MaxVariableKeys}. Counted against the run's real bag of " +
            $"{run.Variables.Count} key(s): this node's dispatch is narrowed to a declared read projection, so the bag " +
            "the dispatcher was given is not what the run actually holds. Overwrite an existing key instead of minting " +
            "a new one, or report fewer, larger-grained variables.";
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

        if (node.Lenses.Count > 0)
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
