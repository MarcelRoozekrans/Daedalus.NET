using Daedalus.Agents.Scheduling;
using Microsoft.Extensions.Options;
using Thalos;
using ZeroAlloc.Results;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     Decorates <see cref="ISubagentRunner"/> to stamp <see cref="SubagentRunRequest.Budget"/> from the same
///     <see cref="DetachedRunOptions"/> configuration <see cref="SubagentRunExecutor"/> reads, before delegating.
/// </summary>
/// <remarks>
///     <see cref="Thalos.Workflow.WorkflowNodeDispatcher"/> never sets a budget on the requests it builds (its
///     decompiled <c>RunNodeAsync</c> only sets <c>AgentId</c>, <c>Task</c>, <c>Caller</c> and
///     <c>RequiredOutcome</c>), so without this every workflow-run agent turn falls through to Thalos's own
///     <c>SubagentOptions.DefaultBudget</c> instead of Daedalus's configured ceiling. That is the one control
///     that bounds what a single unattended turn may spend — <c>maxVisits</c> only bounds how many times a node
///     runs, not what each run costs — and it was missing from the only code path that runs agents unattended,
///     in a loop, with no human watching. Ruled to be closed in Daedalus rather than in Thalos: this needs no
///     Thalos release, and it makes the workflow and detached-run paths derive their budget from one
///     configuration source instead of two that could drift.
///     <para>
///     Requires <see cref="DaedalusSchedulingServiceCollectionExtensions.AddDaedalusScheduling"/> to have been
///     called on the same host — that is what binds <see cref="DetachedRunOptions"/> from the
///     <c>DetachedRuns</c> configuration section. Both hosts that call
///     <see cref="Daedalus.Agents.DaedalusAgentsServiceCollectionExtensions.AddDaedalusAgents(Microsoft.Extensions.DependencyInjection.IServiceCollection, Microsoft.Extensions.Configuration.IConfiguration, Microsoft.Extensions.Hosting.IHostEnvironment, Microsoft.Extensions.AI.IEmbeddingGenerator{string, Microsoft.Extensions.AI.Embedding{float}}?)"/> —
///     <c>Daedalus.Api</c> and <c>Daedalus.Cli</c> — also call <c>AddDaedalusScheduling</c>, so this resolves
///     the exact same <see cref="IOptions{TOptions}"/> registration <see cref="SubagentRunExecutor"/> does, not
///     a second binding of the same section that could disagree with it. This type is only ever actually
///     constructed where the workflow engine itself is enabled (<c>Thalos:Workflow:Enabled</c>, false on
///     <c>Daedalus.Cli</c> — see <see cref="Daedalus.Agents.WorkflowConfig.Enabled"/>'s own remarks for why),
///     but the configuration coupling holds on either host regardless.
///     </para>
///     <para>
///     Only stamps a budget when the request left one unset (<see langword="null"/>) — the same rule
///     <see cref="SubagentRunRequest.Budget"/>'s own doc states for "a value set here always wins over the host
///     default": this decorator never overrides a budget a caller deliberately supplied.
///     </para>
/// </remarks>
internal sealed class BudgetedSubagentRunner(ISubagentRunner inner, IOptions<DetachedRunOptions> options) : ISubagentRunner
{
    private readonly ISubagentRunner _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly DetachedRunOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc />
    public ValueTask<Result<AgentTurnResult, AgentError>> RunAsync(SubagentRunRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var budgeted = request.Budget is null
            ? request with { Budget = new SubagentBudget(_options.MaxTotalTokens, TimeSpan.FromSeconds(_options.DeadlineSeconds)) }
            : request;

        return _inner.RunAsync(budgeted, ct);
    }
}
