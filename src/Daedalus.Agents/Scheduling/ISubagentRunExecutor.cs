using Thalos;
using ZeroAlloc.Results;

namespace Daedalus.Agents.Scheduling;

/// <summary>
///     The one seam through which a scheduled run reaches an LLM. <see cref="SubagentRunExecutor"/> is the only
///     type in Daedalus permitted to reference <c>ISubagentRunner</c> — every step dispatcher goes through this
///     interface instead.
/// </summary>
public interface ISubagentRunExecutor
{
    /// <summary>
    ///     Resolves <paramref name="agentName"/> against the agent catalogue and runs it, as the detached
    ///     principal named by <paramref name="principalId"/> and <paramref name="roles"/>, with no parent turn.
    /// </summary>
    /// <param name="agentName">The configured agent name, matched case-insensitively.</param>
    /// <param name="task">The task text handed to the agent as its turn input.</param>
    /// <param name="principalId">The <see cref="ZeroAlloc.Authorization.ISecurityContext.Id"/> the run reports as its caller.</param>
    /// <param name="roles">The roles the run's caller carries.</param>
    /// <param name="ct">Cancels the underlying turn.</param>
    /// <returns>
    ///     The assistant's turn text on success, or the <see cref="AgentError"/> the run failed with — never a
    ///     thrown exception, so a caller can report the outcome instead of retrying a doomed turn.
    /// </returns>
    ValueTask<Result<string, AgentError>> RunAsync(
        string agentName, string task, string principalId, IReadOnlyList<string> roles, CancellationToken ct);
}
