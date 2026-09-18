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
///     turn eight times over, with nobody told until it dead-letters. Two distinct failure shapes both end there: a
///     <see cref="ZeroAlloc.Results.Result{TValue,TError}"/> failure from <see cref="ISubagentRunExecutor.RunAsync"/>
///     is routed to <c>FailAsync</c> directly, and an exception the store itself throws while persisting the
///     result is caught and routed the same way — see the <c>try</c>/<c>catch</c> around
///     <see cref="ScheduledRunExecutionStore.TryCompleteScoutAsync"/> below for why that second path exists.
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

        try
        {
            await store.TryCompleteScoutAsync(message.ExecutionId, result.Value, ct).ConfigureAwait(false);
        }
        // Excludes OperationCanceledException: a normal host shutdown cancels ct mid-call, and without this
        // filter that cancellation would detour through a doomed FailAsync attempt on the same cancelled
        // token instead of propagating - this restores the behaviour from before this catch existed, where a
        // cancellation propagated directly rather than being caught here at all.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // TryCompleteScoutAsync only throws for a database failure (ScheduledRunExecutionStore's own
            // remarks document that non-concurrency exceptions are deliberately left to escape it). FailAsync
            // does not cost the retry benefit here: it only queues a ChannelMessageQueued OUTBOX ROW, never
            // touching the channel adapter itself. If the database is genuinely down, FailAsync's own
            // BeginTransactionAsync throws too and this exception still propagates, preserving today's outbox
            // retry exactly as before. If the database is reachable but the write is permanently rejected (an
            // oversized digest against the payload column, a serializer fault), this turns an eight-attempt
            // retry-to-dead-letter loop nobody is ever told about into an immediate Failed row with the
            // operator notice already queued - a bare throw here would instead re-run the paid scout turn on
            // every retry for a failure no retry can fix.
            await store.FailAsync(message.ExecutionId, Describe(ex), ct).ConfigureAwait(false);
        }
    }

    private static string Describe(AgentError error) => $"{error.Code}: {error.Message}";

    private static string Describe(Exception exception) => $"{exception.GetType().Name}: {exception.Message}";

    [LoggerMessage(EventId = 453, Level = LogLevel.Warning,
        Message = "RunScoutStep references execution {ExecutionId}, which does not exist.")]
    private static partial void LogUnknownExecution(ILogger logger, Guid executionId);
}

/// <summary>
///     Runs the writer over the persisted scout findings and hands its digest to the store, which persists it and
///     enqueues the deliver step. Same no-throw shape as <see cref="RunScoutStepDispatcher"/>, including the
///     <c>try</c>/<c>catch</c> around the store's completion call - see that type's remarks for why it exists.
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

        try
        {
            await store.TryCompleteWriterAsync(message.ExecutionId, result.Value, ct).ConfigureAwait(false);
        }
        // See RunScoutStepDispatcher's catch for why OperationCanceledException is excluded from this filter.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // See RunScoutStepDispatcher's matching catch: FailAsync only queues an outbox row, so a genuine
            // database outage still propagates from FailAsync's own BeginTransactionAsync, while a permanent
            // write rejection is surfaced immediately instead of re-running the paid writer turn eight times.
            await store.FailAsync(message.ExecutionId, Describe(ex), ct).ConfigureAwait(false);
        }
    }

    private static string Describe(AgentError error) => $"{error.Code}: {error.Message}";

    private static string Describe(Exception exception) => $"{exception.GetType().Name}: {exception.Message}";

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
///     Same <c>try</c>/<c>catch</c> shape as <see cref="RunScoutStepDispatcher"/> and
///     <see cref="RunWriterStepDispatcher"/> around the store's completion call, for the same reason: a
///     <see cref="ScheduledRunExecutionStore.TryCompleteDeliveryAsync"/> exception is a database failure, and
///     <see cref="ScheduledRunExecutionStore.FailAsync"/> only queues a <see cref="Channels.ChannelMessageQueued"/>
///     outbox row rather than touching the channel adapter - so a genuine database outage still propagates from
///     <c>FailAsync</c>'s own <c>BeginTransactionAsync</c>, preserving the outbox retry, while a permanent write
///     rejection reaches <see cref="RunStep.Failed"/> with the operator told instead of retrying to exhaustion.
///     This step makes no subagent call, so - unlike the other two - there is no paid turn the catch is
///     protecting; it exists purely so a permanently broken write here is surfaced immediately rather than
///     dead-lettered silently. The store's own tests separately cover the narrower case of a channel writer that
///     is broken for every attempt: <c>A_failure_to_write_the_channel_message_leaves_the_step_un_advanced</c>
///     asserts the row stays at <see cref="RunStep.Deliver"/> when <c>TryCompleteDeliveryAsync</c> throws, which
///     is exactly the exception this dispatcher's catch now converts into an explicit <see cref="RunStep.Failed"/>.
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

        try
        {
            await store.TryCompleteDeliveryAsync(message.ExecutionId, ct).ConfigureAwait(false);
        }
        // The "is not OperationCanceledException" filter matters, not just style: a normal host shutdown
        // cancels ct mid-call, and without this filter that cancellation would detour through FailAsync on
        // the same cancelled token instead of propagating - restoring the behaviour from before this catch
        // existed, where a cancellation propagated directly.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await store.FailAsync(message.ExecutionId, Describe(ex), ct).ConfigureAwait(false);
        }
    }

    private static string Describe(Exception exception) => $"{exception.GetType().Name}: {exception.Message}";

    [LoggerMessage(EventId = 455, Level = LogLevel.Warning,
        Message = "DeliverDigest references execution {ExecutionId}, which does not exist.")]
    private static partial void LogUnknownExecution(ILogger logger, Guid executionId);
}
