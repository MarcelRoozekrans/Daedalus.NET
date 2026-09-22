using Microsoft.Extensions.DependencyInjection;
using Thalos;
using Thalos.Workflow;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     Constructs <see cref="WorkflowNodeDispatcher"/> — the one place besides
///     <see cref="Daedalus.Agents.Scheduling.SubagentRunExecutor"/> permitted to touch Thalos's raw
///     <see cref="ISubagentRunner"/>, per <c>CleanArchitectureTests.OnlySubagentRunExecutor_DependsOn_ISubagentRunner</c>.
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
/// </remarks>
internal static class WorkflowNodeDispatcherFactory
{
    public static WorkflowNodeDispatcher Create(IServiceProvider sp) => new(
        sp.GetRequiredService<IWorkflowStore>(),
        sp.GetRequiredService<ISubagentRunner>(),
        sp.GetRequiredService<IWorkflowReferenceResolver>(),
        sp.GetRequiredService<IProcessDefinitionStore>(),
        resolveCaller: run => new WorkflowCaller(run));
}
