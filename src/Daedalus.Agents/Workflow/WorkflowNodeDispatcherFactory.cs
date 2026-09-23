using Daedalus.Agents.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Thalos;
using Thalos.Workflow;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     Constructs <see cref="WorkflowNodeDispatcher"/> — one of two places besides
///     <see cref="Daedalus.Agents.Scheduling.SubagentRunExecutor"/> permitted to touch Thalos's raw
///     <see cref="ISubagentRunner"/>, per <c>CleanArchitectureTests.OnlySubagentRunExecutor_DependsOn_ISubagentRunner</c>
///     — the other is <see cref="BudgetedSubagentRunner"/>, wrapped in here.
/// </summary>
/// <remarks>
///     <see cref="Daedalus.Agents.Scheduling.ISubagentRunExecutor"/> is not a substitute here: it resolves an agent by <em>name</em> and
///     returns plain text, which is exactly right for a scheduling step but cannot express what
///     <see cref="WorkflowNodeDispatcher"/> needs — an already-resolved <see cref="AgentId"/> (from
///     <see cref="IWorkflowReferenceResolver"/>) and a <c>RequiredOutcome</c> tool schema constraining the turn's
///     result. <see cref="WorkflowNodeDispatcher"/> is Thalos's own constrained-outcome dispatcher, built the
///     same way <see cref="Daedalus.Agents.Scheduling.SubagentRunExecutor"/> is: directly over
///     <see cref="ISubagentRunner"/>. Isolating the construction call here, rather than inline in the composition
///     root, is what keeps <c>DaedalusAgentsServiceCollectionExtensions</c> itself off the single-seam rule's
///     offender list — the rule flags at the referencing <em>type</em>, not the call site within it.
///     <para>
///     <b>The runner is wrapped in <see cref="BudgetedSubagentRunner"/>, not passed through raw.</b>
///     <see cref="WorkflowNodeDispatcher"/> never sets <c>SubagentRunRequest.Budget</c> itself, so an unwrapped
///     <see cref="ISubagentRunner"/> here would leave every workflow-run turn on Thalos's global default budget
///     instead of Daedalus's configured one — see <see cref="BudgetedSubagentRunner"/>'s own remarks for the
///     full reasoning. This is what makes the exception on the single-seam rule true rather than a loophole:
///     the workflow path now applies the same budget policy <see cref="Daedalus.Agents.Scheduling.SubagentRunExecutor"/>
///     applies to detached runs, from the same configuration, rather than skipping it.
///     </para>
/// </remarks>
internal static class WorkflowNodeDispatcherFactory
{
    public static WorkflowNodeDispatcher Create(IServiceProvider sp) => new(
        // The store the DISPATCHER sees, not the one everything else does. WorkflowRunModeStore stamps the
        // squad mode and the turn's recall tier onto every transition it records; ReviewHandoffWorkflowStore
        // cuts the implementer's narrative out of the bag before a review node's task text is built from it,
        // and re-checks that node's report against the bag the run really holds, because the cut takes
        // Thalos' own key-cap check out of the loop on exactly that node. Only the dispatcher's copy is
        // wrapped deliberately: WorkflowRunGateway, WorkflowRunReconciler and the sweeper all keep the
        // undecorated store, so a human reading a run still sees everything it holds.
        //
        // ORDER IS LOAD-BEARING on CompleteNodeAsync, and it was not before the re-check existed. With
        // ReviewHandoffWorkflowStore outermost, the report it counts is the node's own; reversing the two
        // would hand it a report WorkflowRunModeStore had already added squad_mode and recall_tier to, and a
        // node would be failed for two keys it never reported. Those two keys are deliberately outside the
        // cap - see WorkflowRunModeStore's own remarks.
        new ReviewHandoffWorkflowStore(
            new WorkflowRunModeStore(
                sp.GetRequiredService<IWorkflowStore>(),
                sp.GetRequiredService<SquadOptions>(),
                sp.GetRequiredService<Daedalus.Agents.Memory.WorkflowRecallTierLog>()),
            sp.GetRequiredService<IProcessDefinitionStore>()),
        // Order matters. ReviewLensRunner is the OUTER decorator and BudgetedSubagentRunner the inner one, so a
        // node that runs three lens passes runs three separately budgeted turns rather than three passes sharing
        // one turn's ceiling. Reversing the two would let a two-lens review exhaust the budget and fail the node
        // on the third, which is a cost control silently becoming a correctness bug.
        new ReviewLensRunner(
            new BudgetedSubagentRunner(sp.GetRequiredService<ISubagentRunner>(), sp.GetRequiredService<IOptions<DetachedRunOptions>>()),
            sp.GetRequiredService<IProcessDefinitionStore>()),
        sp.GetRequiredService<IWorkflowReferenceResolver>(),
        sp.GetRequiredService<IProcessDefinitionStore>(),
        resolveCaller: run => new WorkflowCaller(run));
}
