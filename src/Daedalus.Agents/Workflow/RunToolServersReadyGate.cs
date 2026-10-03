using System.Text.Json;
using Daedalus.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Thalos;
using Thalos.Mcp;
using Thalos.Sandbox;
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
///     <para>
///     <b>A failed sandbox restore is recorded, not refused (phase 2.6, task B5).</b> In sandbox mode a ready sandbox
///     has attempted its package restore, and a failed one does not fail the wait: the run's agent may be the one to fix
///     it. Once the wait succeeds, the gate reads the sandbox's readiness once per run, through
///     <paramref name="sandboxReadiness"/>, and a failed restore appends one <see cref="WorkflowRunRecord.SandboxRestoreKind"/>
///     record with the restore's output tail; the run proceeds either way. <paramref name="restores"/> keeps the read to
///     one per run while its sandbox lives, and the record store keeps the record to one per run. Nothing about the read
///     or the record can refuse the dispatch: a read or a write that fails is logged and given back, so the next node
///     tries again.
///     </para>
/// </remarks>
/// <param name="workspaces">Finds the run's workspace.</param>
/// <param name="config">The write grants and the readiness timeout.</param>
/// <param name="readiness">
///     <see langword="null"/> when no run-scoped MCP server is configured on this host, so a run has nothing to wait for
///     (rulings R24 and R27). The missing-workspace check still applies.
/// </param>
/// <param name="sandboxReadiness">
///     The sandbox provider's <see cref="SandboxRunWorkspaceProvider.ReadinessAsync"/> when the host's workspace provider
///     is a <see cref="SandboxRunWorkspaceProvider"/>, and <see langword="null"/> in local mode, which has no restore to
///     record.
/// </param>
/// <param name="restores">Which runs' restore state was already read.</param>
/// <param name="scopes">Creates the scope the record store is resolved from.</param>
/// <param name="clock">Timestamps the record.</param>
/// <param name="logger">Logs a restore state that could not be read or recorded.</param>
internal sealed partial class RunToolServersReadyGate(
    IRunWorkspaceProvider workspaces,
    WorkflowConfig config,
    IRunToolServerReadiness? readiness,
    Func<Guid, CancellationToken, ValueTask<Result<SandboxReadiness, AgentError>>>? sandboxReadiness,
    SandboxRestoreLedger restores,
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<RunToolServersReadyGate> logger)
    : IWorkflowDispatchGate
{
    /// <summary>What <see cref="SandboxReadiness.Restore"/> says when the sandbox's restore failed.</summary>
    internal const string RestoreFailed = "failed";

    /// <summary>
    ///     The <see cref="WorkflowRunRecord.PrincipalId"/> of a restore record: the run's sandbox restored, and no caller
    ///     asked for it.
    /// </summary>
    internal const string SandboxPrincipalId = "sandbox";

    private readonly IRunWorkspaceProvider _workspaces = workspaces ?? throw new ArgumentNullException(nameof(workspaces));
    private readonly WorkflowConfig _config = config ?? throw new ArgumentNullException(nameof(config));
    private readonly SandboxRestoreLedger _restores = restores ?? throw new ArgumentNullException(nameof(restores));
    private readonly IServiceScopeFactory _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly ILogger<RunToolServersReadyGate> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

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

        if (!ready.IsSuccess)
        {
            return Result.Failure($"run tool servers not ready: {ready.Error.Message}");
        }

        if (sandboxReadiness is not null)
        {
            await RecordFailedRestoreOnceAsync(sandboxReadiness, run, node, ct).ConfigureAwait(false);
        }

        return Result.Success();
    }

    /// <summary>
    ///     Reads the run's sandbox readiness, if no node of the run has yet, and records a failed restore. Never fails the
    ///     dispatch: only the dispatch's own cancellation leaves this method as an exception.
    /// </summary>
    private async ValueTask RecordFailedRestoreOnceAsync(
        Func<Guid, CancellationToken, ValueTask<Result<SandboxReadiness, AgentError>>> read, WorkflowRun run, string node, CancellationToken ct)
    {
        if (!_restores.TryClaim(run.Id))
        {
            return;
        }

        var settled = false;
        try
        {
            settled = await ReadAndRecordAsync(read, run, node, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Any failure other than the dispatch's own cancellation, including a database error from the append: the
            // run proceeds, and the claim is given back below so the next node tries again.
            LogRestoreNotRecorded(_logger, ex, run.Id);
        }
        finally
        {
            if (!settled)
            {
                _restores.Release(run.Id);
            }
        }
    }

    /// <summary>True once the run's restore state was read, and recorded when it failed; false to read it again.</summary>
    private async ValueTask<bool> ReadAndRecordAsync(
        Func<Guid, CancellationToken, ValueTask<Result<SandboxReadiness, AgentError>>> read, WorkflowRun run, string node, CancellationToken ct)
    {
        var state = await read(run.Id, ct).ConfigureAwait(false);
        if (state.IsFailure)
        {
            LogRestoreUnread(_logger, run.Id, state.Error.Message);
            return false;
        }

        if (!string.Equals(state.Value.Restore, RestoreFailed, StringComparison.Ordinal))
        {
            return true;
        }

        await using var scope = _scopes.CreateAsyncScope();
        var records = scope.ServiceProvider.GetRequiredService<IWorkflowRunRecordStore>();

        // Once per run across host restarts and sandboxes too: the ledger is in memory and forgets a parked sandbox.
        var recorded = await records.ListAsync(run.Id, WorkflowRunRecord.SandboxRestoreKind, ct).ConfigureAwait(false);
        if (recorded is { Count: > 0 })
        {
            return true;
        }

        // jsonb refuses a NUL even escaped, and the tail is the sandbox's own process output.
        var detail = state.Value.RestoreDetail?.Replace("\0", "", StringComparison.Ordinal);
        var payload = JsonSerializer.Serialize(new { restore = RestoreFailed, detail });
        var record = WorkflowRunRecord.Create(
            run.Id, run.CurrentSeq, node, WorkflowRunRecord.SandboxRestoreKind, SandboxPrincipalId, run.StartedBy?.Id, payload,
            _clock.GetUtcNow().UtcDateTime);
        if (record.IsFailure)
        {
            // Not transient: the same values would be refused at the next node, so the claim is kept.
            LogRestoreRejected(_logger, run.Id, record.Error);
            return true;
        }

        await records.AppendAsync(record.Value, ct).ConfigureAwait(false);
        LogRestoreFailedRecorded(_logger, run.Id);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Run {RunId}'s sandbox failed to restore; recorded, and the run proceeds")]
    private static partial void LogRestoreFailedRecorded(ILogger logger, Guid runId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Run {RunId}'s sandbox restore state could not be read ({Reason}); the next node tries again")]
    private static partial void LogRestoreUnread(ILogger logger, Guid runId, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Run {RunId}'s failed sandbox restore could not be recorded; the next node tries again")]
    private static partial void LogRestoreNotRecorded(ILogger logger, Exception exception, Guid runId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Run {RunId}'s failed sandbox restore record was invalid ({Reason}); it is not recorded")]
    private static partial void LogRestoreRejected(ILogger logger, Guid runId, string reason);
}
