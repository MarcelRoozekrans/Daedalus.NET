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
///     register a second <c>OutboxWorkerService</c> racing the same table. ZeroAlloc.Outbox 3.0.1's source confirms
///     why that warning is not overcautious: <c>AddOutbox()</c> itself registers <c>OutboxOptions</c> and
///     <c>AddHostedService&lt;OutboxWorkerService&gt;()</c> as ordinary <c>Add</c> calls; it is
///     <c>WithEfCore&lt;T&gt;()</c>/<c>WithOrm()</c> — called next, on the builder <c>AddOutbox()</c> returns —
///     that each register <c>IOutboxStore</c> as an ordinary <c>AddScoped</c>, not <c>TryAddScoped</c>, and not
///     keyed. <c>OutboxWorkerService</c> then resolves <c>IOutboxStore</c> as a single <c>GetRequiredService</c>
///     per batch — the last <c>AddScoped</c> registration wins for <em>every</em> <c>OutboxWorkerService</c>
///     instance in the container, regardless of which one that particular worker was built to poll. A second
///     <c>AddOutbox().WithOrm()</c> call here would therefore silently replace the EfCore-backed store both the
///     existing poller and this one resolve, stopping the live RepoDigest chain while double-polling the workflow
///     table. <c>docs/workflow.md</c>'s Step 4 snippet assumes a host with no outbox pipeline yet; Daedalus
///     already has one, so this is a small, self-contained poller instead — the same reasoning
///     <see cref="Daedalus.Agents.Scheduling.ScheduleSweeperService"/> gives for hand-rolling its own timer rather
///     than forcing <c>ZeroAlloc.Scheduling</c> into a shape it does not fit. It shares no DI registration with the
///     existing pipeline: <see cref="OrmOutboxStore"/> is constructed directly here, per batch, never added to this
///     container's <c>IOutboxStore</c> slot.
///     </para>
///     <para>
///     <b>Mirrors <c>OutboxWorkerService</c> 3.0.1's lease loop</b> — see <see cref="ProcessBatchAsync"/> — and its
///     retry/backoff/dead-letter shape (exponential backoff off
///     <see cref="WorkflowOutboxDispatchOptions.RetryBaseDelay"/>, dead-lettering at
///     <see cref="WorkflowOutboxDispatchOptions.MaxAttempts"/>) so this table behaves the way an operator already
///     expects an outbox table to behave. The claim is atomic and row-locked, so any number of replicas may poll
///     this table: two of them never dispatch the same message, unless one dispatch outlives
///     <see cref="WorkflowOutboxDispatchOptions.LeaseDuration"/>, which <see cref="WorkflowDispatchTiming.Validate"/>
///     rules out at registration.
///     </para>
///     <para>
///     <b>It dead-letters every row it does not recognise, and that is only safe while it is the sole
///     consumer.</b> <see cref="ProcessBatchAsync"/> dead-letters any entry whose <c>TypeName</c> is not
///     <see cref="WorkflowDispatchOutboxDispatcher.TypeName"/>, rather than leaving it for someone else, because
///     the workflow store is currently the only writer of the <c>outboxmessages</c> table in this database.
///     The channel/scheduling pipeline is not a second writer of it: EF Core's migrations create a
///     <em>quoted</em>, mixed-case <c>"OutboxMessages"</c> and <c>ZeroAlloc.Outbox.Orm</c>'s create an unquoted
///     <c>OutboxMessages</c> that PostgreSQL folds to <c>outboxmessages</c>, so the two are distinct tables —
///     asserted by <c>WorkflowOrmMigrationTests.The_EF_and_ORM_outbox_tables_coexist_as_distinct_tables</c>.
///     The moment Milestone 3
///     moves any other producer onto <c>ZeroAlloc.Outbox.Orm</c>, this poller will silently eat its messages.
///     Whoever makes that move has to give this loop a dispatcher registry keyed by <c>TypeName</c>, or scope
///     its claim to rows it owns, before the second producer ships.
///     </para>
///     <para>
///     <b>A tick must never let an exception escape</b> — the same rule
///     <see cref="Daedalus.Agents.Scheduling.ScheduleSweeperService"/> documents: doing so would stop this
///     <see cref="BackgroundService"/>'s loop and end every future poll, not just the failing one. Only a
///     cancellation of the stopping token ends the loop.
///     </para>
/// </remarks>
internal sealed partial class WorkflowOutboxDispatchService(
    NpgsqlDataSource dataSource,
    IOutboxTypeDispatcher dispatcher,
    WorkflowOutboxDispatchOptions options,
    ILogger<WorkflowOutboxDispatchService> logger) : BackgroundService
{
    /// <summary>
    ///     The budget for store bookkeeping that runs whether or not the host is stopping: recording the outcome
    ///     of a dispatch that ran, and releasing the leases a batch did not finish. The same five seconds
    ///     <c>OutboxWorkerService</c> gives it.
    /// </summary>
    private static readonly TimeSpan BookkeepingTimeout = TimeSpan.FromSeconds(5);

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
            catch (Exception ex) when (!IsStopping(ex, stoppingToken))
            {
                // A cancellation that did not come from the stopping token, such as a timeout inside the store,
                // is a failed batch, not a request to stop the host.
                LogBatchFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>
    ///     Runs exactly one claim-renew-dispatch-mark cycle. Internal, not private — the same test seam
    ///     <see cref="Daedalus.Agents.Scheduling.ScheduleSweeperService"/> exposes for driving a sweep on demand
    ///     instead of waiting on the real <see cref="WorkflowOutboxDispatchOptions.PollingInterval"/> — an
    ///     integration test drives this directly against a real Postgres outbox table.
    /// </summary>
    /// <remarks>
    ///     The batch is claimed under one lease, and each entry's lease is renewed right before it is handled; an
    ///     entry whose lease was lost is skipped, because another replica may own it now. Once a dispatch has run,
    ///     its outcome is recorded on <see cref="BookkeepingTimeout"/>, never on <paramref name="ct"/>: a graceful
    ///     stop that cancelled the mark would leave the message pending, and it would be dispatched a second time.
    ///     When the batch ends early, because the host is stopping or a store call failed, the leases on the
    ///     entries not yet dispatched are released so another replica can claim them at once.
    /// </remarks>
    internal async Task ProcessBatchAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

        // The Postgres dialect, not the constructor's SQLite default: only its claim locks the batch with
        // FOR UPDATE SKIP LOCKED, so concurrent replicas pass over each other's rows instead of queuing on them.
        var store = new OrmOutboxStore(connection.AsAsync(), OutboxOrmDialect.Postgres);
        var lease = new OutboxLease(options.HostId, options.LeaseDuration);
        var entries = await store.ClaimPendingAsync(options.BatchSize, lease, ct).ConfigureAwait(false);

        // The index of the first entry this host has not finished with. It moves past an entry once that
        // entry's dispatch has run, before its outcome is recorded, so a batch that ends early never releases a
        // message that was already dispatched.
        var next = 0;
        try
        {
            while (next < entries.Count)
            {
                ct.ThrowIfCancellationRequested();
                var entry = entries[next];

                // Renew before acting on the message at all, including dead-lettering it. If the lease ran out
                // while earlier entries were handled, another replica may already own this message.
                if (!await store.RenewLeaseAsync(entry.Id, lease, ct).ConfigureAwait(false))
                {
                    next++;
                    LogLeaseLost(logger, entry.Id.Value, entry.TypeName);
                    continue;
                }

                if (!string.Equals(entry.TypeName, dispatcher.TypeName, StringComparison.Ordinal))
                {
                    LogNoDispatcher(logger, entry.TypeName, entry.Id.Value);
                    if (!await store.DeadLetterAsync(entry.Id, $"No dispatcher for type '{entry.TypeName}'.", lease, ct).ConfigureAwait(false))
                    {
                        LogCompletedElsewhere(logger, entry.Id.Value, entry.TypeName, "dead-lettered");
                    }

                    next++;
                    continue;
                }

                Exception? failure = null;
                try
                {
                    await dispatcher.DispatchAsync(entry.Payload, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (!IsStopping(ex, ct))
                {
                    // Includes a cancellation the dispatch raised on its own, such as a turn's deadline: that is a
                    // failed attempt, retried with backoff like any other failure, not a stop.
                    failure = ex;
                }

                // The dispatch ran to an outcome, so the batch is done with this entry: releasing it now would
                // let another replica dispatch it again.
                next++;
                await RecordOutcomeAsync(store, entry, lease, failure).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            await ReleaseUnfinishedAsync(store, entries, next, lease, ex, ct).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    ///     Records the outcome of a dispatch that ran, on a budget of its own rather than the stopping token. A
    ///     mark that returns <see langword="false"/> means the message is no longer pending under this host's
    ///     lease — another replica claimed it after the lease ran out, or it was completed elsewhere — which is
    ///     logged, not treated as a failure.
    /// </summary>
    private async Task RecordOutcomeAsync(OrmOutboxStore store, OutboxEntry entry, OutboxLease lease, Exception? failure)
    {
        using var bookkeeping = new CancellationTokenSource(BookkeepingTimeout);

        if (failure is null)
        {
            if (!await store.MarkSucceededAsync(entry.Id, lease, bookkeeping.Token).ConfigureAwait(false))
            {
                LogCompletedElsewhere(logger, entry.Id.Value, entry.TypeName, "succeeded");
            }

            return;
        }

        var newRetryCount = entry.RetryCount + 1;
        if (newRetryCount >= options.MaxAttempts)
        {
            LogDeadLettered(logger, entry.Id.Value, entry.TypeName, options.MaxAttempts, failure);
            if (!await store.DeadLetterAsync(entry.Id, failure.Message, lease, bookkeeping.Token).ConfigureAwait(false))
            {
                LogCompletedElsewhere(logger, entry.Id.Value, entry.TypeName, "dead-lettered");
            }

            return;
        }

        var delay = TimeSpan.FromMilliseconds(options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, newRetryCount - 1));
        LogRetrying(logger, entry.Id.Value, entry.TypeName, newRetryCount, options.MaxAttempts, failure);
        if (!await store.MarkFailedAsync(entry.Id, newRetryCount, DateTimeOffset.UtcNow.Add(delay), lease, bookkeeping.Token).ConfigureAwait(false))
        {
            LogCompletedElsewhere(logger, entry.Id.Value, entry.TypeName, "failed");
        }
    }

    /// <summary>
    ///     Best-effort release of the leases on <paramref name="entries"/> from index <paramref name="from"/> on.
    ///     A failure is logged, not thrown: the leases then simply run out after
    ///     <see cref="WorkflowOutboxDispatchOptions.LeaseDuration"/>, and the caller's exception is the one that
    ///     surfaces.
    /// </summary>
    private async Task ReleaseUnfinishedAsync(
        OrmOutboxStore store,
        IReadOnlyList<OutboxEntry> entries,
        int from,
        OutboxLease lease,
        Exception cause,
        CancellationToken stoppingToken)
    {
        if (from >= entries.Count)
        {
            return;
        }

        var ids = new OutboxMessageId[entries.Count - from];
        for (var i = from; i < entries.Count; i++)
        {
            ids[i - from] = entries[i].Id;
        }

        var stopping = IsStopping(cause, stoppingToken);

        // The stopping token may already be cancelled, so the release gets its own budget.
        using var bookkeeping = new CancellationTokenSource(BookkeepingTimeout);
        try
        {
            var released = await store.ReleaseLeasesAsync(ids, lease, bookkeeping.Token).ConfigureAwait(false);
            LogReleased(logger, stopping, released, ids.Length);
        }
        catch (Exception ex)
        {
            LogReleaseFailed(logger, stopping, ids.Length, lease.Duration, ex);
        }
    }

    /// <summary>
    ///     True only for a cancellation caused by <paramref name="stoppingToken"/>, which means the host is
    ///     stopping. Any other exception, including a cancellation a dispatch or the store raised on its own, is
    ///     an ordinary failure.
    /// </summary>
    private static bool IsStopping(Exception ex, CancellationToken stoppingToken) =>
        ex is OperationCanceledException && stoppingToken.IsCancellationRequested;

    [LoggerMessage(EventId = 1801, Level = LogLevel.Error, Message = "Workflow outbox poll batch failed; the next poll will retry.")]
    private static partial void LogBatchFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1802, Level = LogLevel.Warning, Message = "No dispatcher for workflow outbox type '{TypeName}' (message {Id}). Dead-lettering.")]
    private static partial void LogNoDispatcher(ILogger logger, string typeName, Guid id);

    [LoggerMessage(EventId = 1803, Level = LogLevel.Error, Message = "Workflow dispatch {Id} ({TypeName}) exhausted {MaxAttempts} attempts. Dead-lettering.")]
    private static partial void LogDeadLettered(ILogger logger, Guid id, string typeName, int maxAttempts, Exception exception);

    [LoggerMessage(EventId = 1804, Level = LogLevel.Warning, Message = "Workflow dispatch {Id} ({TypeName}) failed (attempt {Attempt}/{Max}).")]
    private static partial void LogRetrying(ILogger logger, Guid id, string typeName, int attempt, int max, Exception exception);

    [LoggerMessage(EventId = 1805, Level = LogLevel.Warning, Message = "The lease on workflow dispatch {Id} ({TypeName}) was lost before dispatch; skipping it. Another replica may own it now.")]
    private static partial void LogLeaseLost(ILogger logger, Guid id, string typeName);

    [LoggerMessage(EventId = 1806, Level = LogLevel.Warning, Message = "Workflow dispatch {Id} ({TypeName}) is no longer pending under this host's lease, so marking it {Outcome} changed nothing. Another replica claimed it after the lease ran out, or it was completed elsewhere.")]
    private static partial void LogCompletedElsewhere(ILogger logger, Guid id, string typeName, string outcome);

    [LoggerMessage(EventId = 1807, Level = LogLevel.Information, Message = "Workflow outbox batch ended early (stopping: {Stopping}); released {Released} of {Unfinished} unfinished leases.")]
    private static partial void LogReleased(ILogger logger, bool stopping, int released, int unfinished);

    [LoggerMessage(EventId = 1808, Level = LogLevel.Warning, Message = "Workflow outbox batch ended early (stopping: {Stopping}); could not release {Count} unfinished leases, which run out after {LeaseDuration}.")]
    private static partial void LogReleaseFailed(ILogger logger, bool stopping, int count, TimeSpan leaseDuration, Exception exception);
}
