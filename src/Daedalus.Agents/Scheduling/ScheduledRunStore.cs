using Cronos;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Outbox;

namespace Daedalus.Agents.Scheduling;

/// <summary>
///     The atomic claim at the heart of the scheduling sweep: finds every enabled <c>ScheduledRun</c> whose
///     <c>NextRunAt</c> is due, advances each to its next occurrence, and enqueues a <see cref="ScheduledRunDue"/>
///     trigger for the most recent due occurrence — all inside one database transaction per sweep, not one per row.
///     A per-row transaction would let a mid-sweep failure roll back the outbox write for one row while an earlier
///     row's advance had already committed, consuming that earlier occurrence with no trigger ever emitted. See
///     <see cref="ClaimAndEnqueueDueAsync"/>'s remarks for the full argument.
/// </summary>
/// <remarks>
///     Scoped, unlike <c>PostgresConversationMap</c> and the other Postgres-backed stores, which are singletons over
///     <see cref="IDbContextFactory{TContext}"/>. This store writes an outbox row inside its own transaction, and
///     ZeroAlloc.Outbox.EfCore's <c>EfCoreOutboxStore&lt;TContext&gt;</c> takes <c>ApplicationDbContext</c> straight
///     from DI and calls <c>Database.UseTransactionAsync(transaction)</c> on it — a call that throws unless that
///     context's own connection is the exact same object as the transaction's connection. A context minted by
///     <see cref="IDbContextFactory{TContext}"/> is, by contract, never that object: the factory always returns an
///     instance "not managed by the DI container," so its connection can never be identical to one resolved through
///     DI elsewhere. Injecting <see cref="ApplicationDbContext"/> directly instead means this store and the
///     constructor-injected <see cref="IOutboxWriter{T}"/>'s internal store resolve the *same* scoped instance
///     (<c>AddDbContextPool&lt;ApplicationDbContext&gt;</c> in <c>AspireExtensions</c> registers it scoped), so they
///     share one connection and <c>UseTransactionAsync</c> actually succeeds. Callers must resolve this store from
///     its own <c>IServiceScope</c> per unit of work, one sweep at a time, rather than holding it for the
///     app's lifetime. Do not "fix" this back to the factory pattern for consistency with the other stores — that
///     silently reintroduces a transaction that can never enlist.
/// </remarks>
public sealed partial class ScheduledRunStore(
    ApplicationDbContext db,
    IOutboxWriter<ScheduledRunDue> outbox,
    TimeProvider time,
    ILogger<ScheduledRunStore> logger)
{
    private readonly ApplicationDbContext _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly IOutboxWriter<ScheduledRunDue> _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    private readonly ILogger<ScheduledRunStore> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    ///     Claims every enabled, due <c>ScheduledRun</c> and enqueues its trigger.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     Everything below runs inside a single transaction covering the whole sweep, committed once at the end.
    ///     <see cref="IOutboxWriter{T}.WriteAsync"/> is handed <c>tx.GetDbTransaction()</c> — the ambient EF
    ///     transaction converted to the ADO.NET <see cref="System.Data.Common.DbTransaction"/> the writer's
    ///     signature takes — rather than <c>null</c>. Passing <c>null</c> compiles (the parameter is nullable) but
    ///     would let the outbox writer commit on its own connection, independent of this sweep's transaction; a
    ///     crash between that write and this method's own <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>
    ///     could then either fire a trigger for an occurrence whose <c>NextRunAt</c> never advanced (a double fire
    ///     on retry) or advance <c>NextRunAt</c> for an occurrence whose trigger was never durably queued (a
    ///     silently skipped run). Passing the real transaction makes the advance and the enqueue one atomic unit:
    ///     they commit together or, on any exception before <see cref="IDbContextTransaction.CommitAsync"/>, roll
    ///     back together — including every row already advanced earlier in the same sweep.
    ///     </para>
    ///     <para>
    ///     <c>ScheduledRun</c> carries an <c>xmin</c> concurrency token (see
    ///     <c>ScheduledRunConfiguration</c>), so two sweepers racing the same due row are already mutually
    ///     exclusive at the database level without any explicit row lock: whichever's row-level <c>UPDATE</c>
    ///     reaches Postgres first wins, and the loser's own <c>UPDATE</c> — issued against the now-stale <c>xmin</c>
    ///     it read — affects zero rows once unblocked, which EF Core surfaces as
    ///     <see cref="DbUpdateConcurrencyException"/>. Because <see cref="IOutboxWriter{T}"/> shares this sweep's
    ///     own <see cref="ApplicationDbContext"/>, its internal <c>SaveChangesAsync</c> call flushes every tracked
    ///     change so far — including an earlier row's <c>AdvanceTo</c> — so this exception can surface mid-loop, not
    ///     only from the trailing <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>; the <c>try</c> below
    ///     wraps the whole loop for exactly that reason. Either way it is caught and treated as "another sweeper
    ///     already claimed this tick", not an error: it rolls back this sweep's whole transaction (including any
    ///     outbox rows already staged for other due runs in the same batch) and returns 0, exactly as if this sweep
    ///     had found nothing due. Only the contested row will not be found due again - the winner already
    ///     advanced its <c>NextRunAt</c>. Every other due row in this sweep rolled back with it too, though, and
    ///     will simply be found due again on the next tick, firing correctly one tick late.
    ///     </para>
    /// </remarks>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of runs claimed and fired.</returns>
    public async ValueTask<int> ClaimAndEnqueueDueAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        await using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var due = await _db.ScheduledRuns
            .Where(r => r.Enabled && r.NextRunAt <= now)
            .ToListAsync(ct).ConfigureAwait(false);

        var fired = 0;
        var committed = false;

        try
        {
            foreach (var run in due)
            {
                var (next, fireAt, missed) = NextOccurrence(run.Cron, run.NextRunAt, now);

                if (next is null)
                {
                    // A syntactically valid but impossible cron (e.g. "0 0 30 2 *") has no future occurrence.
                    // Cronos returns null rather than throwing or looping forever, but DateTime.MaxValue
                    // (Kind=Unspecified) cannot be persisted into a "timestamp with time zone" column - Npgsql
                    // rejects it - and even if it could, "next run in year 9999" is a disabled schedule wearing
                    // a disguise. Disable it honestly instead: this is the only mutation for this row, so it
                    // still commits atomically with every other row in this sweep, and it does not stop those
                    // other rows from firing.
                    run.Disable();
                    LogNoFutureOccurrence(_logger, run.Name, run.Cron);
                    continue;
                }

                run.AdvanceTo(next.Value, now, missed);

                // IOutboxWriter<T>.WriteAsync shares this sweep's own ApplicationDbContext (see the class
                // remarks), so EfCoreOutboxStore's internal SaveChangesAsync call - triggered by THIS write,
                // for THIS row - flushes every change tracked so far, including the AdvanceTo above. A
                // concurrency loss on this row's xmin can therefore surface right here, mid-loop, not only
                // from the trailing SaveChangesAsync below - which is why the catch wraps the whole loop, not
                // just the tail.
                await _outbox.WriteAsync(new ScheduledRunDue(run.Id, fireAt), tx.GetDbTransaction(), ct)
                    .ConfigureAwait(false);
                fired++;
            }

            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            committed = true;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            LogConcurrencyLoss(_logger, ex);
            return 0;
        }
        finally
        {
            // Rows advanced earlier in this loop are still tracked as Unchanged (their database rows just rolled
            // back), the losing row is still tracked as Modified, and any OutboxMessageEntity staged by the last
            // WriteAsync is still tracked as Added - none of that reflects reality once the transaction is gone.
            // A caller reusing this scope must not resolve those stale tracked entities by identity on its next
            // read, or insert a trigger on its next save for an occurrence this sweep never actually claimed. This
            // finally covers every non-committed exit, not only the concurrency catch above: a review of the
            // sibling ScheduledRunExecutionStore found that guarding only the concurrency path left any OTHER
            // exception out of WriteAsync or SaveChangesAsync free to propagate with the tracker still dirty, and
            // this store had the identical gap.
            if (!committed)
            {
                _db.ChangeTracker.Clear();
            }
        }

        if (fired > 0)
        {
            LogClaimed(_logger, fired);
        }

        return fired;
    }

    [LoggerMessage(EventId = 440, Level = LogLevel.Information, Message = "Claimed and enqueued {Count} due scheduled run(s).")]
    private static partial void LogClaimed(ILogger logger, int count);

    [LoggerMessage(EventId = 441, Level = LogLevel.Warning,
        Message = "Scheduled run {Name} (cron {Cron}) has no future occurrence; disabling it instead of advancing forever.")]
    private static partial void LogNoFutureOccurrence(ILogger logger, string name, string cron);

    [LoggerMessage(EventId = 442, Level = LogLevel.Information,
        Message = "Sweep lost a concurrency race to another sweeper claiming the same due run(s); yielding this tick.")]
    private static partial void LogConcurrencyLoss(ILogger logger, DbUpdateConcurrencyException exception);

    /// <summary>
    ///     Walks <paramref name="cron"/> forward with Cronos from <paramref name="dueSince"/> (the schedule's
    ///     stored <c>NextRunAt</c>) to find every occurrence at or before <paramref name="now"/> that has
    ///     accumulated since then, plus the first one after it.
    /// </summary>
    /// <remarks>
    ///     When more than one occurrence has piled up (e.g. after downtime), the <em>most recent</em> one is the
    ///     one that fires — <paramref name="dueSince"/>'s replacement, returned as <c>FireAt</c> — and every older
    ///     one is counted in <c>Missed</c>, never fired. Firing the oldest instead would invert spec D8's own
    ///     reasoning: "yesterday's digest has no value today" is exactly why catch-up-all was rejected, and firing
    ///     the stale first missed day while silently discarding today's occurrence does precisely that. A schedule
    ///     that has been due for three days therefore fires the most recent (third) day's occurrence once and
    ///     reports <c>Missed == 3</c> — the three occurrences (including the original stale <paramref
    ///     name="dueSince"/>) that accumulated before it, none of which fire.
    ///     <c>Next</c> is <see langword="null"/> when the cron has no occurrence after <paramref name="now"/> at
    ///     all — a syntactically valid but impossible expression such as <c>0 0 30 2 *</c> — which the caller
    ///     handles by disabling the schedule rather than persisting an unrepresentable "next run" value.
    /// </remarks>
    /// <param name="cron">The schedule's cron expression, parsed here (never in the domain — see <c>ScheduledRun</c>).</param>
    /// <param name="dueSince">The schedule's stored <c>NextRunAt</c> before this call - the earliest occurrence known to be due.</param>
    /// <param name="now">The current instant, from <see cref="TimeProvider"/>.</param>
    /// <returns>
    ///     The next occurrence strictly after <paramref name="now"/> (or <see langword="null"/> if the cron has
    ///     none), the most recent occurrence at or before <paramref name="now"/> that should fire, and how many
    ///     older occurrences were skipped.
    /// </returns>
    private static (DateTime? Next, DateTime FireAt, int Missed) NextOccurrence(string cron, DateTime dueSince, DateTime now)
    {
        var expression = CronExpression.Parse(cron);
        var cursor = dueSince;
        var fireAt = dueSince;
        var missed = 0;

        while (true)
        {
            var candidate = expression.GetNextOccurrence(cursor, TimeZoneInfo.Utc);
            if (candidate is null)
            {
                return (null, fireAt, missed);
            }

            if (candidate > now)
            {
                return (candidate.Value, fireAt, missed);
            }

            missed++;
            fireAt = candidate.Value;
            cursor = candidate.Value;
        }
    }
}
