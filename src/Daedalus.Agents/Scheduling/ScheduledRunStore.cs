using Cronos;
using Daedalus.Agents.Channels;
using Daedalus.Infrastructure.Persistence.Orm;
using Daedalus.Infrastructure.Persistence.Orm.ScheduledRuns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZeroAlloc.ORM;
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
///     Scoped, as the channel pipeline's keyed writers it takes are resolved per scope; callers resolve this store
///     from their own <c>IServiceScope</c> per sweep. Each sweep opens one connection from <see cref="DaedalusDb"/>
///     and one transaction on it. The constructor-injected <see cref="IOutboxWriter{T}"/> is the channel pipeline's,
///     keyed by <see cref="ChannelOutboxServiceCollectionExtensions.PipelineName"/>. It is handed that transaction's
///     ADO.NET <see cref="System.Data.Common.DbTransaction"/>, and ZeroAlloc.Outbox.Orm's store inserts on that
///     transaction's own connection, so the trigger commits or rolls back with the sweep (rulings R114 and R115).
/// </remarks>
public sealed partial class ScheduledRunStore(
    DaedalusDb db,
    [FromKeyedServices(ChannelOutboxServiceCollectionExtensions.PipelineName)] IOutboxWriter<ScheduledRunDue> outbox,
    TimeProvider time,
    ILogger<ScheduledRunStore> logger)
{
    private readonly DaedalusDb _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly IOutboxWriter<ScheduledRunDue> _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    private readonly ILogger<ScheduledRunStore> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    ///     Claims every enabled, due <c>ScheduledRun</c> and enqueues its trigger.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     Everything below runs inside a single transaction covering the whole sweep, committed once at the end.
    ///     <see cref="IOutboxWriter{T}.WriteAsync"/> is handed the sweep's transaction, as the ADO.NET
    ///     <see cref="System.Data.Common.DbTransaction"/> the writer's signature takes, rather than <c>null</c>.
    ///     Passing <c>null</c> compiles (the parameter is nullable) but would let the outbox writer commit on its own
    ///     connection, independent of this sweep's transaction; a crash between that write and the row's own update
    ///     could then either fire a trigger for an occurrence whose <c>NextRunAt</c> never advanced (a double fire
    ///     on retry) or advance <c>NextRunAt</c> for an occurrence whose trigger was never durably queued (a
    ///     silently skipped run). Passing the real transaction makes the advance and the enqueue one atomic unit:
    ///     they commit together or, on any exception before the commit, roll back together — including every row
    ///     already advanced earlier in the same sweep.
    ///     </para>
    ///     <para>
    ///     Every write of a row is guarded by the <c>xmin</c> the sweep read it with, the token EF Core kept on
    ///     <c>ScheduledRun</c>, so two sweepers racing the same due row are mutually exclusive at the database level
    ///     without any explicit row lock: whichever's row-level <c>UPDATE</c> reaches Postgres first wins, and the
    ///     loser's own <c>UPDATE</c> — issued against the now-stale <c>xmin</c> it read — affects zero rows once
    ///     unblocked, which surfaces as <see cref="ZeroAllocOrmConcurrencyException"/>. A row's trigger is enqueued
    ///     before its update, as EF Core wrote the update last, at the trailing save, so the row lock is taken as late
    ///     as it was. The <c>try</c> below wraps the whole loop, the enqueues included, so any failure in it rolls the
    ///     sweep back. A concurrency loss is caught and treated as "another sweeper already claimed this tick", not an
    ///     error: it rolls back this sweep's whole transaction (including any outbox rows already staged for other due
    ///     runs in the same batch) and returns 0, exactly as if this sweep had found nothing due. Only the contested
    ///     row will not be found due again - the winner already advanced its <c>NextRunAt</c>. Every other due row in
    ///     this sweep rolled back with it too, though, and will simply be found due again on the next tick, firing
    ///     correctly one tick late. The rows are read by id, so two sweeps take their row locks in one order.
    ///     </para>
    /// </remarks>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of runs claimed and fired.</returns>
    public async ValueTask<int> ClaimAndEnqueueDueAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var sql = new ScheduledRunSql(connection);

        var due = await sql.ListDueAsync(now, tx, ct).ConfigureAwait(false);

        var fired = 0;

        try
        {
            foreach (var (run, version) in due)
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
                    await sql.DisableAsync(run.Id, version, tx, ct).ConfigureAwait(false);
                    LogNoFutureOccurrence(_logger, run.Name, run.Cron);
                    continue;
                }

                run.AdvanceTo(next.Value, now, missed);

                // The trigger is inserted on this sweep's transaction (see the class remarks), then the row's
                // guarded update, where a concurrency loss surfaces and takes the trigger with it.
                await _outbox.WriteAsync(new ScheduledRunDue(run.Id, fireAt), tx.AsDbTransaction(), ct)
                    .ConfigureAwait(false);
                await sql.AdvanceAsync(run, version, tx, ct).ConfigureAwait(false);
                fired++;
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (ZeroAllocOrmConcurrencyException ex)
        {
            // Disposing the uncommitted transaction rolls the whole sweep back.
            LogConcurrencyLoss(_logger, ex);
            return 0;
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
    private static partial void LogConcurrencyLoss(ILogger logger, ZeroAllocOrmConcurrencyException exception);

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
