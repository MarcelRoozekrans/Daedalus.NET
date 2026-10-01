using Thalos.Mcp;
using Thalos.Workflow;
using Thalos.Workspaces;
using ZeroAlloc.Results;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     The dispatch gate before every task node's turn: the run's workspace must exist when the node holds a write grant,
///     and every run-scoped MCP server of the run must be ready, within <see cref="WorkflowConfig.RoslynReadyTimeout"/>.
///     A refusal fails the run with a message naming the node; nothing is dispatched.
/// </summary>
/// <remarks>
///     <para>
///     <b>Registered on every workflow host.</b> Thalos's own sample registers its gate only when a <c>runScoped</c>
///     entry exists, because that gate needs <see cref="IRunToolServerReadiness"/>, which only such an entry registers.
///     This one takes <paramref name="readiness"/> as optional instead, so the missing-workspace check below (ruling R9)
///     holds on a host with no run-scoped server too.
///     </para>
///     <para>
///     <b>Cancellation.</b> A timeout is a failed result from <see cref="IRunToolServerReadiness.WaitAllReadyAsync"/>. An
///     <see cref="OperationCanceledException"/> leaves this gate only when the dispatch's own token was cancelled: one that
///     escaped the wait for any other reason would be retried by the outbox as a failed attempt and strand the run with
///     no message, so it is turned into a refusal here (the contract on <see cref="IWorkflowDispatchGate"/>).
///     </para>
/// </remarks>
/// <param name="workspaces">Finds the run's workspace.</param>
/// <param name="config">The write grants and the readiness timeout.</param>
/// <param name="readiness">
///     <see langword="null"/> when no run-scoped MCP server is configured on this host, so a run has nothing to wait for
///     (rulings R24 and R27). The missing-workspace check still applies.
/// </param>
internal sealed class RunToolServersReadyGate(IRunWorkspaceProvider workspaces, WorkflowConfig config, IRunToolServerReadiness? readiness)
    : IWorkflowDispatchGate
{
    private readonly IRunWorkspaceProvider _workspaces = workspaces ?? throw new ArgumentNullException(nameof(workspaces));
    private readonly WorkflowConfig _config = config ?? throw new ArgumentNullException(nameof(config));

    /// <inheritdoc />
    public async ValueTask<Result> BeforeTaskNodeAsync(WorkflowRun run, string node, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (await _workspaces.FindAsync(run.Id, ct).ConfigureAwait(false) is null)
        {
            // Fail closed (ruling R9): a node that holds a write grant must never run without its worktree. A run
            // whose workspace was swept, or never made, would otherwise reach implement and fail only at publish,
            // after a human had approved it.
            return _config.WriteGrants.Any(g => string.Equals(g.Process, run.Process, StringComparison.Ordinal)
                                               && string.Equals(g.Node, node, StringComparison.Ordinal))
                ? Result.Failure($"run has no workspace, and node '{node}' holds a write grant that needs one")
                : Result.Success(); // an ungranted node of a run with no workspace: nothing to wait for
        }

        // No run-scoped server is configured on this host, e.g. a test host whose Thalos:McpConfigPath declares none:
        // the run has nothing to wait for (ruling R24). The workspace check above still applies (ruling R9).
        if (readiness is null)
        {
            return Result.Success();
        }

        UnitResult<Thalos.AgentError> ready;
        try
        {
            ready = await readiness.WaitAllReadyAsync(run.Id, _config.RoslynReadyTimeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            return Result.Failure($"run tool servers not ready: the wait was cancelled without the dispatch being cancelled ({ex.Message})");
        }

        return ready.IsSuccess ? Result.Success() : Result.Failure($"run tool servers not ready: {ready.Error.Message}");
    }
}
