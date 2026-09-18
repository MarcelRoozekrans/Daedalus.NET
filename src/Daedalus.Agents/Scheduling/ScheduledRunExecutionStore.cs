using System.Data.Common;
using Daedalus.Agents.Channels;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Outbox;

namespace Daedalus.Agents.Scheduling;

/// <summary>
///     The correctness core of the saga-free scheduling redesign: one row per firing of a
///     <see cref="ScheduledRun"/> (<see cref="ScheduledRunExecution"/>), advanced one step at a time. This store
///     replaces what a <c>ZeroAlloc.Saga</c> orchestrator would have held in memory — the saga was found
///     undriveable (it never receives its trigger event) and dropped; see the phase's design notes. The row is the
///     saga's state, and the <c>UNIQUE (ScheduleId, OccurrenceAt)</c> index (see
///     <c>ScheduledRunExecutionConfiguration</c>) is the saga's old correlation key, now enforced by the database
///     rather than by any handler's diligence.
/// </summary>
/// <remarks>
///     <para>
///     Scoped, for exactly the reason <see cref="ScheduledRunStore"/> is scoped — see that type's remarks for the
///     full argument, reproduced here because it binds this store too: this store writes an outbox row inside its
///     own transaction, and <c>EfCoreOutboxStore&lt;ApplicationDbContext&gt;</c>'s only constructor takes
///     <see cref="ApplicationDbContext"/> straight from DI and calls <c>Database.UseTransactionAsync(transaction)</c>
///     on it — a call that throws unless that context's own connection is the exact same object as the
///     transaction's connection. A context minted by <c>IDbContextFactory&lt;ApplicationDbContext&gt;</c> can never
///     be that object, so this store injects <see cref="ApplicationDbContext"/> directly instead, sharing the one
///     scoped instance with the constructor-injected <see cref="IOutboxWriter{T}"/>s. Do not "fix" this back to the
///     factory pattern for consistency with the other, singleton-over-factory stores — that silently reintroduces a
///     transaction that can never enlist. Callers must resolve this store from its own <c>IServiceScope</c> per unit
///     of work, one dispatched message at a time, rather than holding it for the app's lifetime.
///     </para>
///     <para>
///     Because the outbox writers share this store's own <see cref="ApplicationDbContext"/>, their internal
///     <c>SaveChangesAsync</c> calls can flush this store's own pending changes from inside a method, not only at
///     the trailing <see cref="DbContext.SaveChangesAsync(CancellationToken)"/> each advance method calls itself.
///     That is why both <see cref="TryAdvanceAsync"/>'s and <see cref="FailAsync"/>'s <c>try</c>/<c>catch</c> for
///     <see cref="DbUpdateConcurrencyException"/> wrap the outbox enqueue and the trailing save together, not just
///     the latter: an earlier version of <see cref="TryAdvanceAsync"/> wrapped only the trailing
///     <c>SaveChangesAsync</c>, and a full-suite run (not the store's tests in isolation — timing under load made
///     the difference) reproduced the concurrency exception escaping uncaught from inside the enqueue call
///     instead. <see cref="FailAsync"/> needs the same shape for the same reason: at-least-once delivery lets a
///     redelivered step command and its original both fail the same execution concurrently, and without this it
///     self-heals only by throwing an unhandled exception out of an outbox dispatcher and burning retry budget to
///     get there. Both methods clear the change tracker in a <c>finally</c> on every path that did not commit —
///     not only the concurrency catch. An earlier version cleared only there, and a review found the gap: any
///     <em>other</em> exception out of <c>enqueueNext</c> or <c>SaveChangesAsync</c> — exactly what
///     <c>A_failure_to_write_the_channel_message_leaves_the_step_un_advanced</c> provokes — propagated with the
///     row still tracked as <c>Modified</c> and <c>Step</c> already mutated in memory, even though the transaction
///     rolled back. A caller reusing this scope (every caller does; see above) that next resolved the same
///     execution by identity got that stale tracked instance back from EF's identity map instead of a fresh row —
///     concretely, the natural dispatcher shape "call <see cref="TryCompleteDeliveryAsync"/>, catch, call
///     <see cref="FailAsync"/> on the same store" would see the stale in-memory <see cref="RunStep.Done"/> and
///     return through <see cref="FailAsync"/>'s own terminal guard without ever queuing the operator notice.
///     Matches <see cref="ScheduledRunStore.ClaimAndEnqueueDueAsync"/>'s own remarks on the same point, and that
///     store received the identical fix for the identical reason.
///     </para>
///     <para>
///     <b>The guarantee this store gives is bounded, not absolute.</b> A crash after a step's subagent returns but
///     before that step's advance commits re-runs the step and pays for it twice — there is no two-phase protocol
///     with the model provider to prevent that. What this store guarantees is narrower and load-bearing: one step
///     can be lost or double-paid; the run itself — which occurrence fired, which step it is on, what each
///     completed step produced — is never lost, and the same occurrence is never started twice.
///     </para>
/// </remarks>
public sealed partial class ScheduledRunExecutionStore(
    ApplicationDbContext dbContext,
    IOutboxWriter<RunScoutStep> scoutWriter,
    IOutboxWriter<RunWriterStep> writerWriter,
    IOutboxWriter<DeliverDigest> deliverWriter,
    IOutboxWriter<ChannelMessageQueued> channelWriter,
    TimeProvider time,
    ILogger<ScheduledRunExecutionStore> logger)
{
    private readonly ApplicationDbContext _db = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly IOutboxWriter<RunScoutStep> _scoutWriter = scoutWriter ?? throw new ArgumentNullException(nameof(scoutWriter));
    private readonly IOutboxWriter<RunWriterStep> _writerWriter = writerWriter ?? throw new ArgumentNullException(nameof(writerWriter));
    private readonly IOutboxWriter<DeliverDigest> _deliverWriter = deliverWriter ?? throw new ArgumentNullException(nameof(deliverWriter));
    private readonly IOutboxWriter<ChannelMessageQueued> _channelWriter = channelWriter ?? throw new ArgumentNullException(nameof(channelWriter));
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    private readonly ILogger<ScheduledRunExecutionStore> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    ///     Every column <see cref="TryBeginAsync"/>'s hand-written <c>INSERT</c> lists, in the exact order its
    ///     <c>VALUES</c> clause supplies them. <c>internal</c> so the column-drift guard test
    ///     (<c>The_insert_statement_lists_every_mapped_column</c>) can compare it against
    ///     <see cref="ScheduledRunExecution"/>'s actual mapped columns — a property added to the entity without a
    ///     matching entry here is inserted as <c>NULL</c> or rejected, and only at run time, since nothing else
    ///     checks this list against the model.
    /// </summary>
    internal static readonly string[] InsertColumnNames =
    [
        "Id", "ScheduleId", "OccurrenceAt", "Step", "Findings", "Digest", "ChannelId",
        "ConversationId", "PrincipalId", "Roles", "Attempts", "LastError", "CreatedAt", "UpdatedAt",
    ];

    private static readonly string InsertColumns =
        string.Join(',', InsertColumnNames.Select(c => $"\"{c}\""));

    // EF Core's ExecuteSqlInterpolatedAsync parameterizes every {} hole in an interpolated FormattableString,
    // including one carrying a ":raw" format specifier — Npgsql/EF Core do not recognise that specifier, and a
    // string argument does not implement IFormattable, so the specifier is silently ignored rather than honoured.
    // Verified against a live database: {InsertColumns:raw} still becomes a bound parameter, and Postgres receives
    // literally `INSERT INTO "ScheduledRunExecutions" (@p0) VALUES (@p1, ...)` — a syntax error (42601, "syntax
    // error at or near '$1'"), not the intended literal column list. ExecuteSqlRawAsync's numbered-placeholder
    // syntax ({0}, {1}, ...) is EF's own substitution mechanism, not string.Format on the SQL text sent to the
    // server: each {N} is still replaced with a real bound parameter, so values stay parameterized while the
    // column list — a compile-time constant, never user input — can be embedded as literal SQL text. This is the
    // corrected mechanism; the guarantee it implements (one statement, ON CONFLICT DO NOTHING, no exception-based
    // control flow) is unchanged from the brief.
    private static readonly string InsertPlaceholders =
        string.Join(", ", Enumerable.Range(0, InsertColumnNames.Length).Select(i => $"{{{i}}}"));

    private static readonly string InsertSql =
        $"""
         INSERT INTO "ScheduledRunExecutions" ({InsertColumns})
         VALUES ({InsertPlaceholders})
         ON CONFLICT ("ScheduleId", "OccurrenceAt") DO NOTHING
         """;

    /// <summary>
    ///     Claims one occurrence: reads the firing <see cref="ScheduledRun"/>, creates its execution row at
    ///     <see cref="RunStep.Scout"/>, and enqueues its first step — all as one atomic <c>INSERT ... ON CONFLICT
    ///     DO NOTHING</c> plus one outbox write, committed together.
    /// </summary>
    /// <param name="due">The trigger raised by <see cref="ScheduledRunStore.ClaimAndEnqueueDueAsync"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    ///     <see langword="true"/> if this call created the execution and enqueued its first step;
    ///     <see langword="false"/> if the schedule no longer exists, is invalid, or the occurrence was already
    ///     claimed (a redelivery of the same <see cref="ScheduledRunDue"/>). <see langword="false"/> is a normal
    ///     outcome, not an error — outbox delivery is at-least-once by design.
    /// </returns>
    public async ValueTask<bool> TryBeginAsync(ScheduledRunDue due, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(due);

        var now = _time.GetUtcNow().UtcDateTime;
        var db = _db; // injected scoped context, shared with the outbox writers — see the class remarks
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var committed = false;

        try
        {
            var schedule = await db.ScheduledRuns
                .AsNoTracking()
                .SingleOrDefaultAsync(r => r.Id == due.ScheduleId, ct)
                .ConfigureAwait(false);

            if (schedule is null)
            {
                // Permanent: retrying cannot make a deleted schedule reappear. Same policy as
                // ChannelMessageQueuedDispatcher's unknown-channel branch — log loudly, treat as handled.
                LogUnknownSchedule(_logger, due.ScheduleId, due.OccurrenceAtUtc);
                return false;
            }

            var created = ScheduledRunExecution.Create(
                schedule.Id, due.OccurrenceAtUtc, schedule.ChannelId, schedule.ConversationId,
                schedule.PrincipalId, schedule.Roles, now);

            if (created.IsFailure)
            {
                LogInvalidSchedule(_logger, schedule.Name, created.Error);
                return false;
            }

            var row = created.Value;
            row.BeginScout(now);

            // INSERT ... ON CONFLICT DO NOTHING rather than SaveChanges-and-catch-23505: in PostgreSQL a
            // constraint violation aborts the whole transaction, so the catch could not then go on to write
            // the outbox row in the same transaction — it would have to roll back and start again. One
            // statement, no exception control flow, and the unique index is the only arbiter.
            // row.Findings, row.Digest, and row.LastError are all null at this point (a freshly created execution has
            // completed no step and never failed) - the null-forgiving operator only tells the compiler that passing
            // null here is intentional, not a bug. ExecuteSqlRawAsync binds each element as a DbParameter, so a null
            // element becomes DBNull, exactly as it would for a normal parameterized query.
            var inserted = await db.Database.ExecuteSqlRawAsync(
                InsertSql,
                [
                    row.Id, row.ScheduleId, row.OccurrenceAt, row.Step.ToString(), row.Findings!,
                    row.Digest!, row.ChannelId, row.ConversationId, row.PrincipalId,
                    string.Join(',', row.Roles), row.Attempts, row.LastError!, row.CreatedAt, row.UpdatedAt,
                ],
                ct).ConfigureAwait(false);

            if (inserted == 0)
            {
                LogRedelivered(_logger, due.ScheduleId, due.OccurrenceAtUtc);
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return false;
            }

            await _scoutWriter.WriteAsync(new RunScoutStep(row.Id), tx.GetDbTransaction(), ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            committed = true;
            return true;
        }
        finally
        {
            // Same shape as TryAdvanceAsync's and FailAsync's finally, for the same reason: _scoutWriter.WriteAsync
            // above is EfCoreOutboxStore, which Adds an OutboxMessageEntity to this store's own ApplicationDbContext
            // and calls SaveChangesAsync itself before this method ever reaches its own tx.CommitAsync. If that
            // save (or anything after it) throws, the transaction rolls back on dispose, but the outbox entity is
            // still tracked as Added in this scope's ChangeTracker. Because the worker's DI scope is per batch, not
            // per message (see the class remarks on why this store's own scope must be per unit of work), that
            // orphaned Added entity would survive into the next message's dispatch and ride along on the worker's
            // own, unrelated SaveChangesAsync — inserting a phantom RunScoutStep outside any transaction, pointing
            // at an execution row that was never created. The other two early-return paths above (unknown schedule,
            // invalid schedule) never Add anything, so this clear is a no-op there; it only matters on the path
            // where WriteAsync ran and something after it failed to commit.
            if (!committed)
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>Advances a scouted execution to the writer step, persisting its findings.</summary>
    public ValueTask<bool> TryCompleteScoutAsync(Guid executionId, string findings, CancellationToken ct) =>
        TryAdvanceAsync(executionId, RunStep.Scout,
            (row, now) => row.RecordFindings(findings, now),
            (row, tx, token) => _writerWriter.WriteAsync(new RunWriterStep(row.Id), tx.GetDbTransaction(), token),
            ct);

    /// <summary>Advances a written execution to the deliver step, persisting its digest.</summary>
    public ValueTask<bool> TryCompleteWriterAsync(Guid executionId, string digest, CancellationToken ct) =>
        TryAdvanceAsync(executionId, RunStep.Writer,
            (row, now) => row.RecordDigest(digest, now),
            (row, tx, token) => _deliverWriter.WriteAsync(new DeliverDigest(row.Id), tx.GetDbTransaction(), token),
            ct);

    /// <summary>
    ///     Advances a delivering execution to <see cref="RunStep.Done"/>, queuing the persisted digest as a
    ///     <see cref="ChannelMessageQueued"/> in the same transaction — the guarantee
    ///     <c>ChannelMessageQueuedDispatcher</c> has been waiting for since phase 1.4.
    /// </summary>
    public ValueTask<bool> TryCompleteDeliveryAsync(Guid executionId, CancellationToken ct) =>
        TryAdvanceAsync(executionId, RunStep.Deliver,
            (row, now) => row.Complete(now),
            // row.Digest! is safe precisely because the step check below already required RunStep.Deliver:
            // RecordDigest is the only transition into Deliver, and it always sets Digest first. ExecutionId is
            // row.Id: the diagnostics page needs to walk from a dead-lettered outbox row back to the execution
            // that produced it, which is only possible if this write site stamps it.
            (row, tx, token) => _channelWriter.WriteAsync(
                new ChannelMessageQueued(row.ChannelId, row.ConversationId, row.Digest!, row.Id), tx.GetDbTransaction(), token),
            ct);

    /// <summary>
    ///     Shared shape for every single-step advance: read the row, check it is still at the step this call
    ///     advances, mutate it, enqueue the next step's outbox message in the same transaction, then save and
    ///     commit together.
    /// </summary>
    /// <param name="executionId">The execution to advance.</param>
    /// <param name="expected">The step this call is only valid from.</param>
    /// <param name="mutate">Applies the domain transition (e.g. <see cref="ScheduledRunExecution.RecordFindings"/>).</param>
    /// <param name="enqueueNext">Writes the next step's outbox message inside the ambient transaction.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    ///     <see langword="true"/> if this call performed the advance; <see langword="false"/> if the execution does
    ///     not exist, is not at <paramref name="expected"/> (a redelivery, or the other side of a race already
    ///     won), or lost an <c>xmin</c> concurrency race between this method's read and its save. All three are
    ///     normal, expected outcomes of at-least-once delivery and concurrent pollers — never exceptions.
    /// </returns>
    private async ValueTask<bool> TryAdvanceAsync(
        Guid executionId,
        RunStep expected,
        Action<ScheduledRunExecution, DateTime> mutate,
        Func<ScheduledRunExecution, IDbContextTransaction, CancellationToken, ValueTask> enqueueNext,
        CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var db = _db; // injected scoped context, shared with the outbox writers — see the class remarks
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var committed = false;

        try
        {
            var row = await db.ScheduledRunExecutions.SingleOrDefaultAsync(e => e.Id == executionId, ct).ConfigureAwait(false);
            if (row is null)
            {
                LogUnknownExecution(_logger, executionId);
                return false;
            }

            if (row.Step != expected)
            {
                // Redelivery, or the other poller won. Not an error: at-least-once delivery makes this routine.
                LogStepAlreadyPast(_logger, executionId, expected, row.Step);
                return false;
            }

            mutate(row, now);

            try
            {
                // enqueueNext is inside this try, not just the trailing SaveChangesAsync below: EfCoreOutboxStore
                // shares this method's own ApplicationDbContext (see the class remarks), so its internal
                // SaveChangesAsync call - triggered by THIS enqueue - flushes the mutate() above right here, before
                // this method ever reaches its own SaveChangesAsync. A concurrency loss on this row's xmin can
                // therefore surface from enqueueNext, not only from the call below; a full-suite run (not just this
                // test class in isolation) reproduced exactly that with Two_pollers_racing_one_step_advance_it_exactly_once
                // throwing DbUpdateConcurrencyException out of enqueueNext when the try/catch only wrapped
                // SaveChangesAsync, matching ScheduledRunStore.ClaimAndEnqueueDueAsync's own remarks on the same hazard.
                await enqueueNext(row, tx, ct).ConfigureAwait(false);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                // xmin moved between the read and the save: the other poller advanced this step first.
                // Roll back and report no-op, exactly as the step check above would have.
                LogLostStepRace(_logger, executionId, expected, ex);
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return false;
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            committed = true;
            return true;
        }
        finally
        {
            // Every path above except the committed one leaves this method with the transaction gone but some
            // entity still tracked - the read row as Unchanged (row is null branch aside, which tracks nothing),
            // as Modified (mutate() ran but the save was never reached, or lost the xmin race), or an outbox row
            // as Added (enqueueNext ran but SaveChangesAsync threw for a reason OTHER than a concurrency loss -
            // the exact gap review round 3 found: only the concurrency catch used to clear the tracker, so any
            // other exception out of enqueueNext or SaveChangesAsync - precisely what
            // A_failure_to_write_the_channel_message_leaves_the_step_un_advanced provokes - propagated with the
            // row still tracked as Modified/Done. A caller reusing this scope (every caller does; the store is
            // scoped per unit of work, not per call) that next reads this same execution by identity would get
            // that stale tracked instance back from EF's identity map instead of a fresh row from the database,
            // regardless of what actually committed. Clearing here, unconditionally, on every non-committed exit
            // - including a plain early return, not only a caught exception - is what closes that gap.
            if (!committed)
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>
    ///     Records a failure and queues an operator notice, in the same transaction. Callable from any
    ///     non-terminal step (see <see cref="ScheduledRunExecution.Fail"/>); a no-op once the execution is already
    ///     <see cref="RunStep.Done"/> or <see cref="RunStep.Failed"/>, so a redelivered failure command cannot
    ///     overwrite a successfully delivered run's record, nor double-notify an operator about the same failure.
    /// </summary>
    public async ValueTask FailAsync(Guid executionId, string error, CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var db = _db; // injected scoped context, shared with the outbox writers — see the class remarks
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var committed = false;

        try
        {
            var row = await db.ScheduledRunExecutions.SingleOrDefaultAsync(e => e.Id == executionId, ct).ConfigureAwait(false);
            if (row is null)
            {
                LogUnknownExecution(_logger, executionId);
                return;
            }

            if (row.Step is RunStep.Done or RunStep.Failed)
            {
                return;
            }

            var schedule = await db.ScheduledRuns.AsNoTracking()
                .SingleOrDefaultAsync(r => r.Id == row.ScheduleId, ct).ConfigureAwait(false);
            var name = schedule?.Name ?? row.ScheduleId.ToString();
            var failedStep = row.Step;

            row.Fail(error, now);

            // The channels design's standing rule, and the subject of both parked phase 1.4 defects: the operator
            // is always told something. A run that fails silently at 07:00 with no live turn to notice is
            // indistinguishable from one that never fired.
            var notice = $"Scheduled run \"{name}\" failed during the {failedStep} step: {error}";

            try
            {
                // Both the enqueue and the trailing save are inside this try, for the same reason TryAdvanceAsync's
                // is (see the class remarks): EfCoreOutboxStore shares this method's own ApplicationDbContext, so
                // its internal SaveChangesAsync - triggered by THIS enqueue - can flush the row.Fail(...) mutation
                // above and raise a concurrency loss from inside the enqueue call, not only from the
                // SaveChangesAsync below. This is reachable, not theoretical: at-least-once delivery lets a
                // redelivered step command and its original both fail the same execution concurrently. It
                // self-heals either way - the loser's own retry no-ops on the row.Step check above, and the winner
                // already wrote the operator notice, so nobody is left uninformed - but uncaught, that self-healing
                // happens by throwing out of an outbox dispatcher and burning retry budget to get there. Catching
                // it here makes the self-healing quiet instead of noisy.
                // ExecutionId is row.Id, same as TryCompleteDeliveryAsync's write site: an operator notice is a
                // failed run's only outbox row, and the diagnostics page needs it correlated too, or a
                // dead-lettered notice would be silently unlinked from the execution it reports on.
                await _channelWriter.WriteAsync(
                    new ChannelMessageQueued(row.ChannelId, row.ConversationId, notice, row.Id), tx.GetDbTransaction(), ct)
                    .ConfigureAwait(false);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                // xmin moved between the read and the save: another dispatcher already failed (or otherwise
                // advanced) this execution first. Not an error - two dispatchers racing to fail the same execution
                // is exactly the at-least-once-delivery case this method exists to survive.
                LogLostFailRace(_logger, executionId, ex);
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return;
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            committed = true;
        }
        finally
        {
            // See TryAdvanceAsync's finally for the full argument. The gap a review found: a non-concurrency
            // exception here (e.g. a permanently broken channel writer) used to propagate with row still tracked
            // as Modified and Step already set to Failed in memory, even though the transaction rolled back. A
            // dispatcher that calls TryCompleteDeliveryAsync, catches the failure, and then calls this method on
            // the same store would, without this fix, resolve the stale tracked instance by identity, see a Step
            // that was never actually persisted, and return through one of the guards above without ever queuing
            // the operator notice the standing rule promises.
            if (!committed)
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>
    ///     Reads one execution by id, untracked. Its only callers today are the three step dispatchers'
    ///     (<see cref="RunScoutStepDispatcher"/>, <see cref="RunWriterStepDispatcher"/>,
    ///     <see cref="DeliverDigestDispatcher"/>) cheap pre-checks before paying for a subagent turn — there is no
    ///     resumption logic that runs after a restart; a crash simply leaves the row at its last persisted step,
    ///     and the next redelivered step command finds it there via this same method.
    /// </summary>
    public async ValueTask<ScheduledRunExecution?> FindAsync(Guid executionId, CancellationToken ct) =>
        await _db.ScheduledRunExecutions.AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == executionId, ct).ConfigureAwait(false);

    [LoggerMessage(EventId = 445, Level = LogLevel.Warning,
        Message = "ScheduledRunDue for schedule {ScheduleId} (occurrence {OccurrenceAt}) references a schedule that no longer exists; treating as handled.")]
    private static partial void LogUnknownSchedule(ILogger logger, Guid scheduleId, DateTime occurrenceAt);

    [LoggerMessage(EventId = 446, Level = LogLevel.Error,
        Message = "Schedule {Name} produced an invalid execution and cannot be started: {Error}")]
    private static partial void LogInvalidSchedule(ILogger logger, string name, string error);

    [LoggerMessage(EventId = 447, Level = LogLevel.Information,
        Message = "Redelivered ScheduledRunDue for schedule {ScheduleId} (occurrence {OccurrenceAt}); execution already exists.")]
    private static partial void LogRedelivered(ILogger logger, Guid scheduleId, DateTime occurrenceAt);

    [LoggerMessage(EventId = 448, Level = LogLevel.Warning,
        Message = "Step command references execution {ExecutionId}, which does not exist.")]
    private static partial void LogUnknownExecution(ILogger logger, Guid executionId);

    [LoggerMessage(EventId = 449, Level = LogLevel.Information,
        Message = "Execution {ExecutionId} is not at {Expected} (it is at {Actual}); redelivery or a lost race, ignoring.")]
    private static partial void LogStepAlreadyPast(ILogger logger, Guid executionId, RunStep expected, RunStep actual);

    [LoggerMessage(EventId = 450, Level = LogLevel.Information,
        Message = "Execution {ExecutionId} lost a concurrency race advancing past {Expected}; another poller won.")]
    private static partial void LogLostStepRace(ILogger logger, Guid executionId, RunStep expected, DbUpdateConcurrencyException exception);

    [LoggerMessage(EventId = 451, Level = LogLevel.Information,
        Message = "Execution {ExecutionId} lost a concurrency race in FailAsync; another dispatcher already failed or advanced it.")]
    private static partial void LogLostFailRace(ILogger logger, Guid executionId, DbUpdateConcurrencyException exception);
}
