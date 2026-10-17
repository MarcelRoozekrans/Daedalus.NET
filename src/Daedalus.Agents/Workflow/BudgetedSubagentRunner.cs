using Daedalus.Agents.Scheduling;
using Microsoft.Extensions.Options;
using Thalos;
using ZeroAlloc.Results;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     Decorates <see cref="ISubagentRunner"/> to stamp <see cref="SubagentRunRequest.Budget"/> before delegating: the
///     token ceiling the agent's own <see cref="TurnBudgetsConfig"/> entry names, and the deadline from the same
///     <see cref="DetachedRunOptions"/> configuration <see cref="SubagentRunExecutor"/> reads.
/// </summary>
/// <remarks>
///     <see cref="Thalos.Workflow.WorkflowNodeDispatcher"/> never sets a budget on the requests it builds (its
///     decompiled <c>RunNodeAsync</c> only sets <c>AgentId</c>, <c>Task</c>, <c>Caller</c> and
///     <c>RequiredOutcome</c>), so without this every workflow-run agent turn falls through to Thalos's own
///     <c>SubagentOptions.DefaultBudget</c> instead of Daedalus's configured ceiling. That is the one control
///     that bounds what a single unattended turn may spend — <c>maxVisits</c> only bounds how many times a node
///     runs, not what each run costs — and it was missing from the only code path that runs agents unattended,
///     in a loop, with no human watching. Ruled to be closed in Daedalus rather than in Thalos: this needs no
///     Thalos release.
///     <para>
///     Phase 2.15: the ceiling is per agent. Until then every workflow turn carried the scout's
///     <c>DetachedRuns:MaxTotalTokens</c>, sized for a short read-only detached run, which an implement or review
///     turn measurably outgrows. The agent's name comes from <see cref="IAgentCatalog"/>; an agent the catalog cannot
///     name runs under <see cref="TurnBudgetsConfig.Default"/>, and Thalos reports the missing agent itself. The
///     deadline still comes from <c>DetachedRuns:DeadlineSeconds</c>, which requires
///     <see cref="DaedalusSchedulingServiceCollectionExtensions.AddDaedalusScheduling"/> on the same host; both hosts
///     that call <c>AddDaedalusAgents</c>, <c>Daedalus.Api</c> and <c>Daedalus.Cli</c>, also call it.
///     </para>
///     <para>
///     Only stamps a budget when the request left one unset (<see langword="null"/>) — the same rule
///     <see cref="SubagentRunRequest.Budget"/>'s own doc states for "a value set here always wins over the host
///     default": this decorator never overrides a budget a caller deliberately supplied.
///     </para>
/// </remarks>
internal sealed class BudgetedSubagentRunner(
    ISubagentRunner inner, IOptions<DetachedRunOptions> options, WorkflowConfig workflow, IAgentCatalog agents) : ISubagentRunner
{
    private readonly ISubagentRunner _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly DetachedRunOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly TurnBudgetsConfig _budgets = workflow?.TurnBudgets ?? throw new ArgumentNullException(nameof(workflow));
    private readonly IAgentCatalog _agents = agents ?? throw new ArgumentNullException(nameof(agents));

    /// <inheritdoc />
    public ValueTask<Result<AgentTurnResult, AgentError>> RunAsync(SubagentRunRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Budget is not null)
        {
            return _inner.RunAsync(request, ct);
        }

        var name = _agents.TryGet(request.AgentId, request.AgentRevision, out var definition) ? definition?.Name : null;
        var budget = new SubagentBudget(_budgets.For(name), TimeSpan.FromSeconds(_options.DeadlineSeconds));
        return _inner.RunAsync(request with { Budget = budget }, ct);
    }
}
