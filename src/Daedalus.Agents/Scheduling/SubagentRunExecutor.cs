using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Thalos;
using ZeroAlloc.Results;

namespace Daedalus.Agents.Scheduling;

/// <summary>
///     The single <see cref="ISubagentRunExecutor"/> implementation, and the only type in Daedalus that touches
///     <see cref="ISubagentRunner"/>. Resolves an agent name against <see cref="IAgentCatalog"/>, runs it as the
///     configured <see cref="DetachedPrincipal"/> with no parent turn, and turns any failure into a
///     <see cref="Result{TValue,TError}"/> rather than a thrown exception.
/// </summary>
/// <remarks>
///     A deadline stops work; a budget settles it. <see cref="DetachedRunOptions.DeadlineSeconds"/> and
///     <see cref="DetachedRunOptions.MaxTotalTokens"/> both flow into <see cref="SubagentBudget"/>, but the budget
///     is a post-hoc check because a turn is buffered and nothing can halt it mid-flight — Thalos converts an
///     overspend into a reported failure after the fact, which is enough to bound the next step even though it
///     cannot cut the current one short.
/// </remarks>
public sealed partial class SubagentRunExecutor(
    ISubagentRunner runner,
    IAgentCatalog catalog,
    IOptions<DetachedRunOptions> options,
    ILogger<SubagentRunExecutor> logger) : ISubagentRunExecutor
{
    private readonly ISubagentRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    private readonly IAgentCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly DetachedRunOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly ILogger<SubagentRunExecutor> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async ValueTask<Result<string, AgentError>> RunAsync(
        string agentName, string task, string principalId, IReadOnlyList<string> roles, CancellationToken ct)
    {
        var definition = _catalog.Agents
            .FirstOrDefault(a => string.Equals(a.Name, agentName, StringComparison.OrdinalIgnoreCase));

        if (definition is null)
        {
            // IAgentCatalog has no name-based lookup — only TryGet(AgentId, out _) — so this scan is the
            // whole resolution. Returning rather than throwing keeps a misconfigured schedule a reportable
            // outcome instead of an outbox retry storm.
            LogUnknownAgent(_logger, agentName);
            return Result<string, AgentError>.Failure(
                AgentError.Validation($"No agent named '{agentName}' is registered."));
        }

        var request = new SubagentRunRequest
        {
            AgentId = definition.Id,
            Task = task,
            Caller = new DetachedPrincipal(principalId, roles),
            Budget = new SubagentBudget(_options.MaxTotalTokens, TimeSpan.FromSeconds(_options.DeadlineSeconds)),
            Depth = 0,                 // no parent turn: the depth guard is about a subagent spawning subagents
            ParentSessionId = null,
        };

        var result = await _runner.RunAsync(request, ct).ConfigureAwait(false);
        return result.IsSuccess
            ? Result<string, AgentError>.Success(result.Value.Text)
            : Result<string, AgentError>.Failure(result.Error);
    }

    [LoggerMessage(EventId = 452, Level = LogLevel.Warning, Message = "No agent named '{AgentName}' is registered; the run cannot start.")]
    private static partial void LogUnknownAgent(ILogger logger, string agentName);
}
