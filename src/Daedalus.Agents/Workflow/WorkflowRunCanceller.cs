using Microsoft.Extensions.Logging;
using Thalos.Workflow;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     The engine's <see cref="IWorkflowRunCanceller"/>: cancels through <see cref="WorkflowRunGateway.CancelAsync"/>, the
///     path <c>WorkflowRunsController.Cancel</c> uses.
/// </summary>
/// <remarks>
///     Each attempt runs under a token of its own that only <see cref="CancelTimeout"/> cancels, never the caller's, as
///     <c>NodeUsageRecorder</c>'s append and the gateway's drop void do. The store reads the run and then updates it; a
///     write by another party in the gap between that read and its update raises <see cref="WorkflowConcurrencyException"/>.
///     That one failure is retried once: the second attempt reads the run fresh, and a cancel past a terminal status is a
///     no-op. Any other failure is not retried.
/// </remarks>
internal sealed partial class WorkflowRunCanceller(WorkflowRunGateway gateway, TimeProvider clock, ILogger<WorkflowRunCanceller> logger)
    : IWorkflowRunCanceller
{
    /// <summary>How long one cancel attempt may take, under a token of its own.</summary>
    internal static readonly TimeSpan CancelTimeout = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    public async ValueTask<bool> CancelAsync(Guid runId, string reason, CancellationToken ct)
    {
        try
        {
            await AttemptAsync(runId, reason).ConfigureAwait(false);
            return true;
        }
        catch (WorkflowConcurrencyException ex)
        {
            LogRetrying(logger, ex, runId);
        }
        catch (Exception ex)
        {
            LogCancelFailed(logger, ex, runId);
            return false;
        }

        try
        {
            await AttemptAsync(runId, reason).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            LogCancelFailed(logger, ex, runId);
            return false;
        }
    }

    private async ValueTask AttemptAsync(Guid runId, string reason)
    {
        using var timeout = new CancellationTokenSource(CancelTimeout, clock);
        await gateway.CancelAsync(runId, reason, timeout.Token).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cancelling run {RunId} lost a race to another write; retrying once")]
    private static partial void LogRetrying(ILogger logger, Exception exception, Guid runId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Run {RunId} could not be cancelled")]
    private static partial void LogCancelFailed(ILogger logger, Exception exception, Guid runId);
}
