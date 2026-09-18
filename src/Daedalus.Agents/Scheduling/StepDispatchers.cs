using Daedalus.Domain.Entities;
using Microsoft.Extensions.Logging;
using Thalos;
using ZeroAlloc.Outbox;

namespace Daedalus.Agents.Scheduling;

/// <summary>
///     Runs the scout and hands its findings to the store, which persists them and enqueues the writer step.
///     No dispatcher in this file throws: every failure path ends in <see cref="ScheduledRunExecutionStore.FailAsync"/>,
///     which records the error and queues the operator notice. A throw would hand the message back to the outbox
///     for eight retries with exponential backoff, re-running the subagent each time — paying for the same failing
///     turn eight times over, with nobody told until it dead-letters.
/// </summary>
public sealed partial class RunScoutStepDispatcher(
    ScheduledRunExecutionStore store,
    ISubagentRunExecutor executor,
    ILogger<RunScoutStepDispatcher> logger) : IOutboxDispatcher<RunScoutStep>
{
    /// <inheritdoc />
    public async ValueTask DispatchAsync(RunScoutStep message, CancellationToken ct)
    {
        var execution = await store.FindAsync(message.ExecutionId, ct).ConfigureAwait(false);
        if (execution is null)
        {
            LogUnknownExecution(logger, message.ExecutionId);
            return;
        }

        // Cheap pre-check before paying for a turn. The store re-checks inside its transaction, which is what
        // actually settles a race; this one only avoids billing for a step that is already done (e.g. a
        // redelivery arriving after a restart already advanced this execution past Scout).
        if (execution.Step != RunStep.Scout) { return; }

        var result = await executor.RunAsync(
            RepoDigestPrompts.ScoutAgent, RepoDigestPrompts.ScoutTask,
            execution.PrincipalId, execution.Roles, ct).ConfigureAwait(false);

        if (result.IsFailure)
        {
            await store.FailAsync(message.ExecutionId, Describe(result.Error), ct).ConfigureAwait(false);
            return;
        }

        await store.TryCompleteScoutAsync(message.ExecutionId, result.Value, ct).ConfigureAwait(false);
    }

    private static string Describe(AgentError error) => $"{error.Code}: {error.Message}";

    [LoggerMessage(EventId = 453, Level = LogLevel.Warning,
        Message = "RunScoutStep references execution {ExecutionId}, which does not exist.")]
    private static partial void LogUnknownExecution(ILogger logger, Guid executionId);
}

/// <summary>
///     Runs the writer over the persisted scout findings and hands its digest to the store, which persists it and
///     enqueues the deliver step. Same no-throw shape as <see cref="RunScoutStepDispatcher"/>.
/// </summary>
public sealed partial class RunWriterStepDispatcher(
    ScheduledRunExecutionStore store,
    ISubagentRunExecutor executor,
    ILogger<RunWriterStepDispatcher> logger) : IOutboxDispatcher<RunWriterStep>
{
    /// <inheritdoc />
    public async ValueTask DispatchAsync(RunWriterStep message, CancellationToken ct)
    {
        var execution = await store.FindAsync(message.ExecutionId, ct).ConfigureAwait(false);
        if (execution is null)
        {
            LogUnknownExecution(logger, message.ExecutionId);
            return;
        }

        // Same cheap pre-check as RunScoutStepDispatcher: the store's transaction is what actually settles a race.
        if (execution.Step != RunStep.Writer) { return; }

        // execution.Findings! is safe only because the step check above already required RunStep.Writer, and
        // RecordFindings (ScheduledRunExecutionStore.TryCompleteScoutAsync) is the only transition into Writer —
        // it always sets Findings before advancing the step, so a row at Writer always has non-null Findings.
        var result = await executor.RunAsync(
            RepoDigestPrompts.WriterAgent, RepoDigestPrompts.WriterTask(execution.Findings!),
            execution.PrincipalId, execution.Roles, ct).ConfigureAwait(false);

        if (result.IsFailure)
        {
            await store.FailAsync(message.ExecutionId, Describe(result.Error), ct).ConfigureAwait(false);
            return;
        }

        await store.TryCompleteWriterAsync(message.ExecutionId, result.Value, ct).ConfigureAwait(false);
    }

    private static string Describe(AgentError error) => $"{error.Code}: {error.Message}";

    [LoggerMessage(EventId = 454, Level = LogLevel.Warning,
        Message = "RunWriterStep references execution {ExecutionId}, which does not exist.")]
    private static partial void LogUnknownExecution(ILogger logger, Guid executionId);
}

/// <summary>
///     Delivers the persisted digest: makes no subagent call at all, only asks the store to queue the
///     <see cref="Channels.ChannelMessageQueued"/> and mark the execution <see cref="RunStep.Done"/> in the same
///     transaction — the guarantee <c>ChannelMessageQueuedDispatcher</c> has been waiting for since phase 1.4.
/// </summary>
/// <remarks>
///     Unlike the other two step dispatchers, a <see cref="ScheduledRunExecutionStore.TryCompleteDeliveryAsync"/>
///     exception is deliberately not caught and turned into <see cref="ScheduledRunExecutionStore.FailAsync"/>
///     here. "No dispatcher throws" protects a paid subagent turn from being re-run eight times by the outbox's
///     retry policy; this step makes no subagent call, so there is nothing expensive to protect. Retrying the
///     transactional advance itself is safe — it is idempotent under the store's own optimistic-concurrency check
///     — so letting the outbox retry a transient write failure here is the correct recovery path, not a gap. The
///     store's own tests already cover the terminal case (a permanently broken channel writer leaves the row at
///     <see cref="RunStep.Deliver"/>, never silently marked delivered).
/// </remarks>
public sealed partial class DeliverDigestDispatcher(
    ScheduledRunExecutionStore store,
    ILogger<DeliverDigestDispatcher> logger) : IOutboxDispatcher<DeliverDigest>
{
    /// <inheritdoc />
    public async ValueTask DispatchAsync(DeliverDigest message, CancellationToken ct)
    {
        var execution = await store.FindAsync(message.ExecutionId, ct).ConfigureAwait(false);
        if (execution is null)
        {
            LogUnknownExecution(logger, message.ExecutionId);
            return;
        }

        // Same cheap pre-check as the other step dispatchers.
        if (execution.Step != RunStep.Deliver) { return; }

        await store.TryCompleteDeliveryAsync(message.ExecutionId, ct).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 455, Level = LogLevel.Warning,
        Message = "DeliverDigest references execution {ExecutionId}, which does not exist.")]
    private static partial void LogUnknownExecution(ILogger logger, Guid executionId);
}
