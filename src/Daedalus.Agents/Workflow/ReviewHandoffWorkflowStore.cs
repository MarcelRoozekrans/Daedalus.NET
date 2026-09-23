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
///     <b>This narrows what a node is told, never what the run stores.</b> <c>CompleteNodeAsync</c> is
///     not overridden, and <c>OrmWorkflowStore</c> re-reads the row inside its own transaction before merging,
///     so the persisted bag still holds <c>summary</c> and <c>rationale</c> for humans and for
///     <c>adjudicate</c>. Two consequences worth naming rather than discovering: a human reading a run through
///     <see cref="WorkflowRunGateway"/> sees the full bag, because only the dispatcher's copy of the store is
///     decorated; and <c>WorkflowNodeDispatcher</c>'s sixteen-key cap check counts the projected bag on a
///     review node, so it undercounts there by exactly the keys withheld.
///     </para>
/// </remarks>
internal sealed class ReviewHandoffWorkflowStore(IWorkflowStore inner, IProcessDefinitionStore definitions)
    : DelegatingWorkflowStore(inner)
{
    private readonly IProcessDefinitionStore _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));

    /// <inheritdoc />
    public override async ValueTask<WorkflowRun?> FindAsync(Guid runId, CancellationToken ct)
    {
        var run = await Inner.FindAsync(runId, ct).ConfigureAwait(false);
        if (run is null || run.Variables.Count == 0)
        {
            return run;
        }

        return await IsReviewNodeAsync(run, ct).ConfigureAwait(false)
            ? run with { Variables = ReviewHandoff.ProjectForReviewNode(run.Variables) }
            : run;
    }

    /// <summary>
    ///     Whether the node the run is currently on declares review lenses. A definition or node that cannot be
    ///     resolved answers <see langword="false"/>: the dispatcher resolves both itself a moment later and
    ///     fails the run with its own message, and a run that is about to fail has no reviewer to isolate.
    /// </summary>
    private async ValueTask<bool> IsReviewNodeAsync(WorkflowRun run, CancellationToken ct)
    {
        var definition = await _definitions.GetAsync(run.Process, run.ProcessVersion, ct).ConfigureAwait(false);
        return definition.IsSuccess
            && definition.Value.Nodes.TryGetValue(run.CurrentNode, out var node)
            && node.Lenses.Count > 0;
    }
}
