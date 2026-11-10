using System.Data.Common;
using Daedalus.Agents.Channels;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence.Orm;
using Daedalus.Infrastructure.Persistence.Orm.ScheduledRunExecutions;
using Daedalus.Infrastructure.Persistence.Orm.ScheduledRuns;
using Daedalus.Infrastructure.Services.GitHub;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZeroAlloc.ORM;
using ZeroAlloc.Outbox;

namespace Daedalus.Agents.Scheduling;

/// <summary>
///     The correctness core of the saga-free scheduling redesign: one row per firing of a
///     <see cref="ScheduledRun"/> (<see cref="ScheduledRunExecution"/>), advanced one step at a time. This store
///     replaces what a <c>ZeroAlloc.Saga</c> orchestrator would have held in memory — the saga was found
///     undriveable (it never receives its trigger event) and dropped; see the phase's design notes. The row is the
///     saga's state, and the <c>UNIQUE (ScheduleId, OccurrenceAt)</c> index
///     (<c>IX_ScheduledRunExecution_Schedule_Occurrence</c>) is the saga's old correlation key, now enforced by the
///     database rather than by any handler's diligence.
/// </summary>
/// <remarks>
///     <para>
///     Scoped, as <see cref="ScheduledRunStore"/> is, because the four writers it takes are the channel pipeline's,
///     keyed by <see cref="ChannelOutboxServiceCollectionExtensions.PipelineName"/>, and resolved per scope. Each
///     writing call opens one connection from <see cref="DaedalusDb"/> and one transaction on it, and hands each
///     writer that transaction's ADO.NET <see cref="DbTransaction"/>; ZeroAlloc.Outbox.Orm's store
///     inserts on that transaction's own connection, so the outbox row commits or rolls back with the row change
///     (rulings R114 and R120). Nothing is cached between calls: every call reads the row afresh, so a caller that
///     reuses this store after a call threw (every dispatcher does: <c>TryCompleteDeliveryAsync</c>, catch, then
///     <see cref="FailAsync"/> on the same store) sees what the database holds, not what the failed call wrote.
///     </para>
///     <para>
///     <see cref="TryAdvanceAsync"/> and <see cref="FailAsync"/> enqueue first and then write the row, guarded by the
///     <c>xmin</c> they read it with, EF Core's shadow token on this entity. EF Core wrote the row at its trailing
///     save, after the enqueue, so the row lock is taken as late as before, and a concurrency loss surfaces from the
///     guarded update as <see cref="ZeroAllocOrmConcurrencyException"/>; the call then rolls back, the enqueued
///     message with it. <see cref="FailAsync"/> needs the same shape: at-least-once delivery lets a redelivered step
///     command and its original both fail the same execution concurrently, and without the catch it self-heals only
///     by throwing an unhandled exception out of an outbox dispatcher and burning retry budget to get there.
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
    DaedalusDb db,
    [FromKeyedServices(ChannelOutboxServiceCollectionExtensions.PipelineName)] IOutboxWriter<RunScoutStep> scoutWriter,
    [FromKeyedServices(ChannelOutboxServiceCollectionExtensions.PipelineName)] IOutboxWriter<RunWriterStep> writerWriter,
    [FromKeyedServices(ChannelOutboxServiceCollectionExtensions.PipelineName)] IOutboxWriter<DeliverDigest> deliverWriter,
    [FromKeyedServices(ChannelOutboxServiceCollectionExtensions.PipelineName)] IOutboxWriter<ChannelMessageQueued> channelWriter,
    TimeProvider time,
    IOptions<GitHubOptions> gitHubOptions,
    ILogger<ScheduledRunExecutionStore> logger)
{
    private readonly DaedalusDb _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly IOutboxWriter<RunScoutStep> _scoutWriter = scoutWriter ?? throw new ArgumentNullException(nameof(scoutWriter));
    private readonly IOutboxWriter<RunWriterStep> _writerWriter = writerWriter ?? throw new ArgumentNullException(nameof(writerWriter));
    private readonly IOutboxWriter<DeliverDigest> _deliverWriter = deliverWriter ?? throw new ArgumentNullException(nameof(deliverWriter));
    private readonly IOutboxWriter<ChannelMessageQueued> _channelWriter = channelWriter ?? throw new ArgumentNullException(nameof(channelWriter));
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    private readonly IOptions<GitHubOptions> _gitHubOptions = gitHubOptions ?? throw new ArgumentNullException(nameof(gitHubOptions));
    private readonly ILogger<ScheduledRunExecutionStore> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

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
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var schedule = await new ScheduledRunSql(connection).FindAsync(due.ScheduleId, tx, ct).ConfigureAwait(false);
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

        // INSERT ... ON CONFLICT DO NOTHING rather than insert-and-catch-23505: in PostgreSQL a constraint violation
        // aborts the whole transaction, so the catch could not then go on to write the outbox row in the same
        // transaction — it would have to roll back and start again. One statement, no exception control flow, and the
        // unique index is the only arbiter.
        if (!await new ScheduledRunExecutionSql(connection).TryInsertAsync(row, tx, ct).ConfigureAwait(false))
        {
            // Disposing the uncommitted transaction rolls it back.
            LogRedelivered(_logger, due.ScheduleId, due.OccurrenceAtUtc);
            return false;
        }

        await _scoutWriter.WriteAsync(new RunScoutStep(row.Id), tx.AsDbTransaction(), ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Advances a scouted execution to the writer step, persisting its findings.</summary>
    public ValueTask<bool> TryCompleteScoutAsync(Guid executionId, string findings, CancellationToken ct) =>
        TryAdvanceAsync(executionId, RunStep.Scout,
            (row, now) => row.RecordFindings(findings, now),
            (row, tx, token) => _writerWriter.WriteAsync(new RunWriterStep(row.Id), tx, token),
            ct);

    /// <summary>Advances a written execution to the deliver step, persisting its digest.</summary>
    public ValueTask<bool> TryCompleteWriterAsync(Guid executionId, string digest, CancellationToken ct) =>
        TryAdvanceAsync(executionId, RunStep.Writer,
            (row, now) => row.RecordDigest(digest, now),
            (row, tx, token) => _deliverWriter.WriteAsync(new DeliverDigest(row.Id), tx, token),
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
                new ChannelMessageQueued(row.ChannelId, row.ConversationId, row.Digest!, row.Id), tx, token),
            ct);

    /// <summary>
    ///     Shared shape for every single-step advance: read the row with its <c>xmin</c>, check it is still at the step
    ///     this call advances, mutate it, enqueue the next step's outbox message in the same transaction, then write
    ///     the row, guarded by that <c>xmin</c>, and commit together.
    /// </summary>
    /// <param name="executionId">The execution to advance.</param>
    /// <param name="expected">The step this call is only valid from.</param>
    /// <param name="mutate">Applies the domain transition (e.g. <see cref="ScheduledRunExecution.RecordFindings"/>).</param>
    /// <param name="enqueueNext">Writes the next step's outbox message on the given transaction.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    ///     <see langword="true"/> if this call performed the advance; <see langword="false"/> if the execution does
    ///     not exist, is not at <paramref name="expected"/> (a redelivery, or the other side of a race already
    ///     won), or lost an <c>xmin</c> concurrency race between this method's read and its write. All three are
    ///     normal, expected outcomes of at-least-once delivery and concurrent pollers — never exceptions.
    /// </returns>
    private async ValueTask<bool> TryAdvanceAsync(
        Guid executionId,
        RunStep expected,
        Action<ScheduledRunExecution, DateTime> mutate,
        Func<ScheduledRunExecution, DbTransaction, CancellationToken, ValueTask> enqueueNext,
        CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var sql = new ScheduledRunExecutionSql(connection);

        var found = await sql.FindAsync(executionId, tx, ct).ConfigureAwait(false);
        if (found is null)
        {
            LogUnknownExecution(_logger, executionId);
            return false;
        }

        var (row, version) = found;
        if (row.Step != expected)
        {
            // Redelivery, or the other poller won. Not an error: at-least-once delivery makes this routine.
            LogStepAlreadyPast(_logger, executionId, expected, row.Step);
            return false;
        }

        mutate(row, now);

        try
        {
            // The message is inserted on this transaction, then the row's guarded update, where a concurrency loss
            // surfaces; disposing the uncommitted transaction then takes the message with it.
            await enqueueNext(row, tx.AsDbTransaction(), ct).ConfigureAwait(false);
            await sql.AdvanceAsync(row, version, tx, ct).ConfigureAwait(false);
        }
        catch (ZeroAllocOrmConcurrencyException ex)
        {
            // xmin moved between the read and the write: the other poller advanced this step first. Report no-op,
            // exactly as the step check above would have.
            LogLostStepRace(_logger, executionId, expected, ex);
            return false;
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
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
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var sql = new ScheduledRunExecutionSql(connection);

        var found = await sql.FindAsync(executionId, tx, ct).ConfigureAwait(false);
        if (found is null)
        {
            LogUnknownExecution(_logger, executionId);
            return;
        }

        var (row, version) = found;
        if (row.Step is RunStep.Done or RunStep.Failed)
        {
            return;
        }

        var schedule = await new ScheduledRunSql(connection).FindAsync(row.ScheduleId, tx, ct).ConfigureAwait(false);
        var name = schedule?.Name ?? row.ScheduleId.ToString();
        var failedStep = row.Step;

        row.Fail(error, now);

        // The channels design's standing rule, and the subject of both parked phase 1.4 defects: the operator
        // is always told something. A run that fails silently at 07:00 with no live turn to notice is
        // indistinguishable from one that never fired.
        var notice = $"Scheduled run \"{name}\" failed during the {failedStep} step: {error}";

        try
        {
            // Both the enqueue and the guarded update are inside this try, as in TryAdvanceAsync (see the class
            // remarks); the concurrency loss on the row.Fail(...) mutation above surfaces from the update, and the
            // rollback takes the notice with it. This is reachable, not theoretical: at-least-once delivery lets a
            // redelivered step command and its original both fail the same execution concurrently. It self-heals
            // either way - the loser's own retry no-ops on the row.Step check above, and the winner already wrote the
            // operator notice, so nobody is left uninformed - but uncaught, that self-healing happens by throwing out
            // of an outbox dispatcher and burning retry budget to get there. Catching it here makes the self-healing
            // quiet instead of noisy.
            // ExecutionId is row.Id, same as TryCompleteDeliveryAsync's write site: an operator notice is a
            // failed run's only outbox row, and the diagnostics page needs it correlated too, or a
            // dead-lettered notice would be silently unlinked from the execution it reports on.
            await _channelWriter.WriteAsync(
                new ChannelMessageQueued(row.ChannelId, row.ConversationId, notice, row.Id), tx.AsDbTransaction(), ct)
                .ConfigureAwait(false);
            await sql.FailAsync(row, version, tx, ct).ConfigureAwait(false);
        }
        catch (ZeroAllocOrmConcurrencyException ex)
        {
            // xmin moved between the read and the write: another dispatcher already failed (or otherwise
            // advanced) this execution first. Not an error - two dispatchers racing to fail the same execution
            // is exactly the at-least-once-delivery case this method exists to survive. Disposing the uncommitted
            // transaction rolls the notice back.
            LogLostFailRace(_logger, executionId, ex);
            return;
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Reads one execution by id. Its only callers today are the three step dispatchers'
    ///     (<see cref="RunScoutStepDispatcher"/>, <see cref="RunWriterStepDispatcher"/>,
    ///     <see cref="DeliverDigestDispatcher"/>) cheap pre-checks before paying for a subagent turn — there is no
    ///     resumption logic that runs after a restart; a crash simply leaves the row at its last persisted step,
    ///     and the next redelivered step command finds it there via this same method.
    /// </summary>
    public async ValueTask<ScheduledRunExecution?> FindAsync(Guid executionId, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        return await new ScheduledRunExecutionSql(connection).FindAsync(executionId, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Reads the repository configured on the firing <see cref="ScheduledRun"/> — what
    ///     <see cref="RunScoutStepDispatcher"/> hands to <see cref="RepoDigestPrompts.ScoutTask"/>. Read straight
    ///     off <see cref="ScheduledRun.Repository"/> rather than copied onto the execution row at claim time,
    ///     unlike <see cref="ScheduledRunExecution.ChannelId"/> and its siblings: a schedule's repository is read
    ///     only once per firing, at the Scout step, so there is no redelivery-consistency argument for carrying a
    ///     second copy of it forward through Writer and Deliver, which never need it.
    /// </summary>
    /// <param name="scheduleId">The schedule that fired.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    ///     The configured repository, or <see langword="null"/> if the schedule no longer exists or never
    ///     configured one — the caller's job to treat as a failure, not this read's.
    /// </returns>
    public async Task<string?> GetScheduleRepositoryAsync(Guid scheduleId, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        var schedule = await new ScheduledRunSql(connection).FindAsync(scheduleId, ct).ConfigureAwait(false);
        return schedule?.Repository;
    }

    /// <summary>
    ///     Resolves the start of the scout's window for one occurrence: the <see cref="ScheduledRunExecution.OccurrenceAt"/>
    ///     of the most recent <see cref="RunStep.Done"/> execution of the same schedule strictly before
    ///     <paramref name="occurrenceAtUtc"/>, or <c>now - <see cref="GitHubOptions.DefaultLookback"/></c> when
    ///     there is none.
    /// </summary>
    /// <remarks>
    ///     Only <see cref="RunStep.Done"/> counts as "the last digest" — a <see cref="RunStep.Failed"/> execution
    ///     reported nothing, so anchoring on it would silently narrow the window to the point of the failure
    ///     instead of correctly widening it back to the last occurrence that actually delivered. This is the fix
    ///     for the bug a fixed 24-hour lookback would have: a run missed at 07:00 and caught by redelivery at
    ///     08:00 must not drop the intervening hour, so the window always reaches back to the last real digest,
    ///     however many occurrences ago that was.
    /// </remarks>
    /// <param name="scheduleId">The schedule whose window is being resolved.</param>
    /// <param name="occurrenceAtUtc">The current occurrence's <c>OccurrenceAt</c>; only strictly earlier executions count as "previous".</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The UTC instant the scout's sweep should start from.</returns>
    public async Task<DateTime> ResolveWindowStartAsync(Guid scheduleId, DateTime occurrenceAtUtc, CancellationToken ct)
    {
        DateTime? previousOccurrenceAt;
        await using (var connection = await _db.OpenAsync(ct).ConfigureAwait(false))
        {
            previousOccurrenceAt = await new ScheduledRunExecutionSql(connection)
                .LatestDoneOccurrenceBeforeAsync(scheduleId, occurrenceAtUtc, ct).ConfigureAwait(false);
        }

        return previousOccurrenceAt ?? _time.GetUtcNow().UtcDateTime - _gitHubOptions.Value.DefaultLookback;
    }

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
    private static partial void LogLostStepRace(ILogger logger, Guid executionId, RunStep expected, ZeroAllocOrmConcurrencyException exception);

    [LoggerMessage(EventId = 451, Level = LogLevel.Information,
        Message = "Execution {ExecutionId} lost a concurrency race in FailAsync; another dispatcher already failed or advanced it.")]
    private static partial void LogLostFailRace(ILogger logger, Guid executionId, ZeroAllocOrmConcurrencyException exception);
}
