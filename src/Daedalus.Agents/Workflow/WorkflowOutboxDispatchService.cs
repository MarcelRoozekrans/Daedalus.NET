using System.Data.Async.Adapters;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.Orm;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     Polls the workflow engine's own outbox table — <see cref="OrmOutboxStore"/> against
///     <see cref="NpgsqlDataSource"/>'s database — and dispatches <see cref="Thalos.Workflow.WorkflowDispatch.TypeName"/>
///     rows through <see cref="WorkflowDispatchOutboxDispatcher"/>. Until something polls it, every workflow run
///     started is a row in this table nobody reads: see <c>docs/workflow.md</c>, "The outbox consumer — the piece
///     with no default".
/// </summary>
/// <remarks>
///     <para>
///     <b>Hand-rolled, not <c>ZeroAlloc.Outbox</c>'s own <c>AddOutbox()</c>/<c>OutboxWorkerService</c>.</b> This
///     host already runs one outbox pipeline — <c>AddChannelOutbox</c>/<c>AddDaedalusScheduling</c> wire
///     <c>AddOutbox().WithEfCore&lt;ApplicationDbContext&gt;()</c> for channel delivery and the RepoDigest
///     scheduling chain, and both of those methods' own remarks warn that a second <c>AddOutbox()</c> call would
///     register a second <c>OutboxWorkerService</c> racing the same table. Decompiling ZeroAlloc.Outbox 2.7.1
///     confirms why that warning is not overcautious: <c>AddOutbox()</c> registers <c>OutboxOptions</c> and
///     <c>IOutboxStore</c> as ordinary <c>Add</c> (not <c>TryAdd</c>) singletons, and <c>OutboxWorkerService</c>
///     resolves <c>IOutboxStore</c> as a single <c>GetRequiredService</c> — the last registration wins for
///     <em>every</em> <c>OutboxWorkerService</c> instance in the container. A second
///     <c>AddOutbox().WithOrm()</c> call here would therefore silently replace the EfCore-backed store both the
///     existing poller and this one resolve, stopping the live RepoDigest chain while double-polling the
///     workflow table. <c>docs/workflow.md</c>'s Step 4 snippet assumes a host with no outbox pipeline yet;
///     Daedalus already has one, so this is a small, self-contained poller instead — the same reasoning
///     <see cref="Daedalus.Agents.Scheduling.ScheduleSweeperService"/> gives for hand-rolling its own timer
///     rather than forcing <c>ZeroAlloc.Scheduling</c> into a shape it does not fit. It shares no DI registration
///     with the existing pipeline: <see cref="OrmOutboxStore"/> is constructed directly here, per batch, never
///     added to this container's <c>IOutboxStore</c> slot.
///     </para>
///     <para>
///     Mirrors <c>OutboxWorkerService</c>'s own retry/backoff/dead-letter shape (exponential backoff off
///     <see cref="WorkflowOutboxDispatchOptions.RetryBaseDelay"/>, dead-lettering at
///     <see cref="WorkflowOutboxDispatchOptions.MaxAttempts"/>) so this table behaves the way an operator already
///     expects an outbox table to behave.
///     </para>
///     <para>
///     <b>A tick must never let an exception escape</b> — the same rule
///     <see cref="Daedalus.Agents.Scheduling.ScheduleSweeperService"/> documents: doing so would stop this
///     <see cref="BackgroundService"/>'s loop and end every future poll, not just the failing one.
///     </para>
/// </remarks>
internal sealed partial class WorkflowOutboxDispatchService(
    NpgsqlDataSource dataSource,
    WorkflowDispatchOutboxDispatcher dispatcher,
    WorkflowOutboxDispatchOptions options,
    ILogger<WorkflowOutboxDispatchService> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.PollingInterval);

        do
        {
            try
            {
                await ProcessBatchAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogBatchFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task ProcessBatchAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        var store = new OrmOutboxStore(connection.AsAsync());

        foreach (var entry in await store.FetchPendingAsync(options.BatchSize, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            if (!string.Equals(entry.TypeName, dispatcher.TypeName, StringComparison.Ordinal))
            {
                LogNoDispatcher(logger, entry.TypeName, entry.Id.Value);
                await store.DeadLetterAsync(entry.Id, $"No dispatcher for type '{entry.TypeName}'.", ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                await dispatcher.DispatchAsync(entry.Payload, ct).ConfigureAwait(false);
                await store.MarkSucceededAsync(entry.Id, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await HandleFailureAsync(store, entry, ex, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleFailureAsync(OrmOutboxStore store, OutboxEntry entry, Exception ex, CancellationToken ct)
    {
        var newRetryCount = entry.RetryCount + 1;
        if (newRetryCount >= options.MaxAttempts)
        {
            LogDeadLettered(logger, entry.Id.Value, entry.TypeName, options.MaxAttempts, ex);
            await store.DeadLetterAsync(entry.Id, ex.Message, ct).ConfigureAwait(false);
            return;
        }

        var delay = TimeSpan.FromMilliseconds(options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, newRetryCount - 1));
        LogRetrying(logger, entry.Id.Value, entry.TypeName, newRetryCount, options.MaxAttempts, ex);
        await store.MarkFailedAsync(entry.Id, newRetryCount, DateTimeOffset.UtcNow.Add(delay), ct).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 1801, Level = LogLevel.Error, Message = "Workflow outbox poll batch failed; the next poll will retry.")]
    private static partial void LogBatchFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1802, Level = LogLevel.Warning, Message = "No dispatcher for workflow outbox type '{TypeName}' (message {Id}). Dead-lettering.")]
    private static partial void LogNoDispatcher(ILogger logger, string typeName, Guid id);

    [LoggerMessage(EventId = 1803, Level = LogLevel.Error, Message = "Workflow dispatch {Id} ({TypeName}) exhausted {MaxAttempts} attempts. Dead-lettering.")]
    private static partial void LogDeadLettered(ILogger logger, Guid id, string typeName, int maxAttempts, Exception exception);

    [LoggerMessage(EventId = 1804, Level = LogLevel.Warning, Message = "Workflow dispatch {Id} ({TypeName}) failed (attempt {Attempt}/{Max}).")]
    private static partial void LogRetrying(ILogger logger, Guid id, string typeName, int attempt, int max, Exception exception);
}
