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
///     trigger for the occurrence just claimed — all inside one database transaction per sweep, not one per row.
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
        foreach (var run in due)
        {
            var occurrence = run.NextRunAt;
            var (next, missed) = NextOccurrence(run.Cron, occurrence, now);

            run.AdvanceTo(next, now, missed);
            await _outbox.WriteAsync(new ScheduledRunDue(run.Id, occurrence), tx.GetDbTransaction(), ct)
                .ConfigureAwait(false);
            fired++;
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);

        if (fired > 0)
        {
            LogClaimed(_logger, fired);
        }

        return fired;
    }

    [LoggerMessage(EventId = 440, Level = LogLevel.Information, Message = "Claimed and enqueued {Count} due scheduled run(s).")]
    private static partial void LogClaimed(ILogger logger, int count);

    /// <summary>
    ///     Walks <paramref name="cron"/> forward with Cronos from the just-claimed <paramref name="occurrence"/> to
    ///     the first occurrence strictly after <paramref name="now"/>, counting every occurrence it stepped past
    ///     along the way as missed. A schedule that has been due for three days fires once (the caller only ever
    ///     sees the current call's occurrence) and reports <c>Missed == 2</c> — see spec D8: catch-up-all would bill
    ///     one subagent run per missed day for output nobody asked to see multiple times.
    /// </summary>
    /// <param name="cron">The schedule's cron expression, parsed here (never in the domain — see <c>ScheduledRun</c>).</param>
    /// <param name="occurrence">The occurrence just claimed (the row's <c>NextRunAt</c> before this call).</param>
    /// <param name="now">The current instant, from <see cref="TimeProvider"/>.</param>
    /// <returns>The next occurrence after <paramref name="now"/>, and how many occurrences were skipped to reach it.</returns>
    private static (DateTime Next, int Missed) NextOccurrence(string cron, DateTime occurrence, DateTime now)
    {
        var expression = CronExpression.Parse(cron);
        var cursor = occurrence;
        var missed = 0;

        while (true)
        {
            var candidate = expression.GetNextOccurrence(cursor, TimeZoneInfo.Utc);
            if (candidate is null)
            {
                return (DateTime.MaxValue, missed); // a one-shot cron with no future
            }

            if (candidate > now)
            {
                return (candidate.Value, missed);
            }

            cursor = candidate.Value;
            missed++;
        }
    }
}
