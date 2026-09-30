using Daedalus.Agents.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Thalos;
using Thalos.Skills;
using Thalos.Workflow;
using ZeroAlloc.Authorization;

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
        // and checks every agent node's report against the bag the run really holds, less the room kept for
        // open-pull-request's keys: the cut takes Thalos' own key-cap check out of the loop on a review node, and
        // Thalos checks a host action's keys only after it ran. Only the dispatcher's copy is
        // wrapped deliberately: WorkflowRunGateway, WorkflowRunReconciler and the sweeper all keep the
        // undecorated store, so a human reading a run still sees everything it holds.
        //
        // ORDER IS LOAD-BEARING on CompleteNodeAsync, and it was not before the re-check existed. With
        // ReviewHandoffWorkflowStore outermost, the report it counts is the node's own; reversing the two
        // would hand it a report WorkflowRunModeStore had already added squad_mode and recall_tier to, and a
        // node would be failed for keys it never reported. The check counts those keys as the host's, never as the
        // node's - see WorkflowRunModeStore's own remarks.
        new ReviewHandoffWorkflowStore(
            new WorkflowRunModeStore(
                sp.GetRequiredService<IWorkflowStore>(),
                sp.GetRequiredService<SquadOptions>(),
                sp.GetRequiredService<Daedalus.Agents.Memory.WorkflowRecallTierLog>()),
            sp.GetRequiredService<IProcessDefinitionStore>()),
        // Order matters, twice over. ReviewLensRunner is the middle decorator and BudgetedSubagentRunner the
        // innermost one, so a node that runs three lens passes runs three separately budgeted turns rather than
        // three passes sharing one turn's ceiling - reversing the two would let a two-lens review exhaust the
        // budget and fail the node on the third, which is a cost control silently becoming a correctness bug.
        // StandingInstructionsRunner is outermost: a review node's pinned skill never matches its eligible set
        // (manufacture-implement, manufacture-retrospect), so it is inert on every lens pass regardless of where
        // it sits, but sitting outside means the eligibility check runs once per node dispatch rather than once
        // per lens pass.
        new StandingInstructionsRunner(
            new ReviewLensRunner(
                new BudgetedSubagentRunner(sp.GetRequiredService<ISubagentRunner>(), sp.GetRequiredService<IOptions<DetachedRunOptions>>()),
                sp.GetRequiredService<IProcessDefinitionStore>(),
                // Each accepted lens pass is recorded through a fresh scope, so this singleton captures no scoped
                // dependency.
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<ReviewLensRunner>>())),
        sp.GetRequiredService<IWorkflowReferenceResolver>(),
        sp.GetRequiredService<IProcessDefinitionStore>(),
        sp.GetRequiredService<ISkillStore>(),
        resolveCaller: CreateCallerResolver(sp),
        // The real registrations, empty until the dispatch gate and the open-pull-request action are registered.
        // The host actions are the same GetServices sequence AddDaedalusWorkflow hands WorkflowReferenceResolver,
        // so an action node that validates at load time is the one this dispatcher can run.
        gates: sp.GetServices<IWorkflowDispatchGate>(),
        hostActions: sp.GetServices<IWorkflowHostAction>());

    /// <summary>
    ///     The one caller resolver: the dispatcher's <c>resolveCaller</c>, and what the write-boundary tests call. Each
    ///     turn's <see cref="WorkflowCaller"/> carries the <c>Thalos:Workflow:WriteGrants</c> entry that grants the run's
    ///     current node, or none; see <see cref="WorkspaceWriteGrant.GrantFor"/>. The grants are read once, from the
    ///     startup-validated config, and never from a run variable.
    /// </summary>
    internal static Func<WorkflowRun, ISecurityContext> CreateCallerResolver(IServiceProvider sp)
    {
        var grants = sp.GetRequiredService<WorkflowConfig>().WriteGrants;
        return run => new WorkflowCaller(run, WorkspaceWriteGrant.GrantFor(grants, run));
    }
}
