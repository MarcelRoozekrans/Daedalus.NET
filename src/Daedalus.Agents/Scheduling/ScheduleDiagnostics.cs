using System.Collections.ObjectModel;
using Daedalus.Agents.Channels;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs.Scheduling;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.EfCore;

namespace Daedalus.Agents.Scheduling;

/// <summary>
///     Answers one question: a digest did not arrive — where did it die? Turns the scheduling tables and the
///     outbox into a single <see cref="RunVerdict"/> per run, so a page and an agent cannot give an operator
///     different answers about the same run.
/// </summary>
/// <remarks>
///     <para>
///     <b>Why this lives in <c>Daedalus.Agents</c>.</b> A run can die in five places and they do not all live in
///     one table: <c>ScheduledRuns</c> for the occurrences that never fired, <c>ScheduledRunExecutions</c> for
///     the ones that stranded or failed, and <c>OutboxMessages</c> for the ones that completed and never
///     arrived. <c>Daedalus.Application</c> — where <see cref="IScheduleDiagnostics"/> and
///     <see cref="RunDiagnosis"/> must live, because it is the only assembly both <c>Daedalus.Web</c> and
///     <c>Daedalus.Api</c> reference — must not reference Infrastructure, and <c>CleanArchitectureTests</c>
///     enforces that. Agents is the only layer holding both the scheduling stores and the outbox.
///     </para>
///     <para>
///     <b>Scoped, taking <see cref="ApplicationDbContext"/> directly rather than an
///     <c>IDbContextFactory</c>.</b> The same rule phase 1.5 established for every store that touches the
///     outbox: <c>EfCoreOutboxStore.EnqueueAsync</c> calls <c>UseTransactionAsync</c>, which requires connection
///     identity with whatever else is in the scope. Nothing here writes, but sharing the scope's context keeps
///     this type registrable next to the stores without a second connection per request.
///     </para>
///     <para>
///     <b>One query per table, never one per schedule.</b> <see cref="GetOverviewAsync"/> issues exactly three
///     reads regardless of how many schedules exist: the schedules, the latest execution of each, and one
///     bounded dead-letter fetch. The naive shape is an N+1 that only becomes visible once there are enough
///     schedules to matter.
///     </para>
///     <para>
///     <b>Degrade, never blank.</b> An outbox read that fails downgrades a completed run to
///     <see cref="RunVerdict.DeliveryUnknown"/> rather than failing the page, and never to
///     <see cref="RunVerdict.Delivered"/> — the page must never claim a delivery it could not confirm. A payload
///     that will not deserialize is skipped, not thrown on.
///     </para>
///     <para>
///     <b>An incomplete read is not a clean one.</b> There are three ways the outbox answer can be short of the
///     truth and they must never collapse together: the read threw, the read was capped before it reached far
///     enough back, or the read genuinely found nothing. The first two both mean "cannot confirm"; only the
///     third means <see cref="RunVerdict.Delivered"/>. Both shortfalls also log, because a truncated answer
///     that renders identically to a complete one is the exact failure this page exists to prevent.
///     </para>
/// </remarks>
/// <param name="db">The scope's context; see the remarks on why it is not a factory.</param>
/// <param name="timeProvider">The only clock this type reads. <c>DateTime.UtcNow</c> appears nowhere.</param>
/// <param name="options">Thresholds and bounds; see <see cref="ScheduleDiagnosticsOptions"/>.</param>
/// <param name="serializer">The same serializer the host registers, so no wire format is assumed here.</param>
/// <param name="logger">Records the reads that degraded, since a degraded verdict is otherwise silent.</param>
public sealed class ScheduleDiagnostics(
    ApplicationDbContext db,
    TimeProvider timeProvider,
    IOptions<ScheduleDiagnosticsOptions> options,
    IOutboxSerializer serializer,
    ILogger<ScheduleDiagnostics> logger) : IScheduleDiagnostics
{
    /// <summary>
    ///     The key every <see cref="ChannelMessageQueued"/> outbox row is stamped with. <c>FullName</c>, not
    ///     <c>AssemblyQualifiedName</c>: the generated writer uses the former, and six existing integration tests
    ///     rely on it. Getting it wrong matches zero rows and reports every run as
    ///     <see cref="RunVerdict.Delivered"/> — silently wrong in exactly the way this page exists to prevent.
    /// </summary>
    private static readonly string _channelMessageTypeName =
        typeof(ChannelMessageQueued).FullName
        ?? throw new InvalidOperationException($"{nameof(ChannelMessageQueued)} has no full name; it cannot be a generic parameter or array type.");

    private readonly ScheduleDiagnosticsOptions _options = options.Value;

    /// <inheritdoc/>
    /// <remarks>
    ///     <para>
    ///     Every schedule, disabled ones included. A disabled schedule is the first of the five ways a digest
    ///     dies, so hiding it would make the most basic cause the hardest to see; it is reported
    ///     <see cref="RunVerdict.Disabled"/> rather than excluded or mislabelled.
    ///     </para>
    ///     <para>
    ///     Each row diagnoses the schedule's CURRENT state, which is not the same question as what its last run
    ///     did — see <see cref="Classify"/> for the precedence and why it matters.
    ///     </para>
    /// </remarks>
    public async ValueTask<IReadOnlyList<RunDiagnosis>> GetOverviewAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Read 1 of 3. No Enabled filter: see the remarks.
        var schedules = await db.ScheduledRuns
            .AsNoTracking()
            .OrderBy(s => s.Name)
            .ToListAsync(ct);

        if (schedules.Count == 0)
        {
            return [];
        }

        var scheduleIds = schedules.ConvertAll(s => s.Id);

        // Read 2 of 3 — the latest execution of every schedule at once. Expressed as "no later occurrence of the
        // same schedule exists" rather than a per-schedule ORDER BY ... LIMIT 1, which is the N+1 this is here to
        // avoid. IX_ScheduledRunExecution_Schedule_Occurrence makes OccurrenceAt a total order within a schedule,
        // so this selects exactly one row per schedule that has any.
        var latestExecutions = await db.ScheduledRunExecutions
            .AsNoTracking()
            .Where(e => scheduleIds.Contains(e.ScheduleId)
                        && !db.ScheduledRunExecutions.Any(later => later.ScheduleId == e.ScheduleId && later.OccurrenceAt > e.OccurrenceAt))
            .ToListAsync(ct);

        var byScheduleId = latestExecutions.ToDictionary(e => e.ScheduleId);

        // Read 3 of 3.
        var deadLetters = await ReadDeadLettersAsync(now, ct);

        return schedules.ConvertAll(s => DiagnoseCurrentState(s, byScheduleId.GetValueOrDefault(s.Id), deadLetters, now));
    }

    /// <inheritdoc/>
    /// <remarks>
    ///     <para>
    ///     Executions only. A schedule that has never been claimed has no history, and this returns an empty list
    ///     rather than synthesising a row: the contract is "the most recent executions", and a caller wanting the
    ///     <see cref="RunVerdict.NotYetDue"/> or <see cref="RunVerdict.Overdue"/> answer gets it from
    ///     <see cref="GetOverviewAsync"/>, which is where it belongs.
    ///     </para>
    ///     <para>
    ///     Each row reports what THAT run did, deliberately without the schedule-level precedence
    ///     <see cref="GetOverviewAsync"/> applies. Disabling a schedule must not rewrite the history of the runs
    ///     that already happened into a wall of <see cref="RunVerdict.Disabled"/>; the drill-down exists to show
    ///     what happened last time, which is exactly the question a switched-off schedule raises.
    ///     </para>
    /// </remarks>
    public async ValueTask<IReadOnlyList<RunDiagnosis>> GetRunHistoryAsync(Guid scheduleId, int take, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var limit = Math.Clamp(take, 1, _options.MaxRunHistory);

        var schedule = await db.ScheduledRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == scheduleId, ct);

        if (schedule is null)
        {
            return [];
        }

        var executions = await db.ScheduledRunExecutions
            .AsNoTracking()
            .Where(e => e.ScheduleId == scheduleId)
            .OrderByDescending(e => e.OccurrenceAt)
            .Take(limit)
            .ToListAsync(ct);

        if (executions.Count == 0)
        {
            return [];
        }

        var deadLetters = await ReadDeadLettersAsync(now, ct);

        return executions.ConvertAll(e => DiagnoseHistoricalRun(schedule, e, deadLetters, now));
    }

    /// <summary>
    ///     The one bounded dead-letter read, keyed back to the executions that produced the messages.
    /// </summary>
    /// <param name="now">The clock reading the window is measured back from.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    ///     The dead letters found, or <see cref="DeadLetterLookup.Unavailable"/> if the read itself failed —
    ///     which is not the same answer as "none found" and must never be treated as one.
    /// </returns>
    private async Task<DeadLetterLookup> ReadDeadLettersAsync(DateTime now, CancellationToken ct)
    {
        // IOutboxDashboardStore is the intended seam, but its OutboxEntry carries no DeadLetterError — it can
        // say WHICH messages died, never why, which is half of what this page reports. Neither route can filter
        // by a payload field in SQL, so both must deserialize candidates; this one also returns the reason.
        // Use the dashboard store's RequeueAsync and CancelAsync when the page offers to resend.
        var typeName = _channelMessageTypeName;
        var since = WindowStart(_options.DeadLetterLookback, now);

        List<DeadLetterRow> rows;
        try
        {
            rows = await db.OutboxMessages
                .AsNoTracking()
                .Where(m => m.TypeName == typeName
                            && m.Status == OutboxMessageStatus.DeadLetter
                            && m.CreatedAt >= since)
                .OrderByDescending(m => m.CreatedAt)
                .Take(_options.DeadLetterScanLimit)
                .Select(m => new DeadLetterRow(m.Payload, m.DeadLetterError, m.RetryCount, m.CreatedAt))
                .ToListAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Everything else the page can report still gets reported; only the delivery half degrades.
            logger.LogWarning(ex, "Could not read dead-lettered {TypeName} messages; completed runs will be reported as {Verdict}.",
                typeName, RunVerdict.DeliveryUnknown);
            return DeadLetterLookup.Unavailable;
        }

        // The cap is the other way this read can be incomplete, and it fails toward the MISLEADING answer: past
        // the limit the dropped rows are the oldest, so their executions would find no dead letter and read
        // Delivered. Remember how far back this scan can actually speak for, and say so out loud — a truncated
        // answer that looks identical to a complete one is the failure mode this whole page exists to prevent.
        DateTime? horizon = null;
        if (rows.Count >= _options.DeadLetterScanLimit)
        {
            horizon = rows[^1].CreatedAt.UtcDateTime;
            logger.LogWarning(
                "Dead-letter scan for {TypeName} hit its limit of {Limit} rows; runs created before {Horizon} " +
                "cannot be confirmed delivered and will be reported as {Verdict}. Raise {Option} or narrow the lookback.",
                typeName, _options.DeadLetterScanLimit, horizon, RunVerdict.DeliveryUnknown, nameof(ScheduleDiagnosticsOptions.DeadLetterScanLimit));
        }

        // Deserialization happens OUTSIDE the try above on purpose: a payload that will not parse is one bad row,
        // not an unreadable outbox, and must not downgrade every completed run to DeliveryUnknown.
        var byExecutionId = new Dictionary<Guid, DeadLetterInfo>();
        foreach (var row in rows)
        {
            ChannelMessageQueued? message;
            try
            {
                message = serializer.Deserialize<ChannelMessageQueued>(row.Payload);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Skipping a dead-lettered {TypeName} row whose payload could not be deserialized.", typeName);
                continue;
            }

            if (message?.ExecutionId is not { } executionId)
            {
                // An ordinary channel reply, not a scheduled delivery. Honest, and nothing to correlate.
                continue;
            }

            // Rows arrive newest first, so the first one wins: the most recent failure is the one an operator is
            // looking at when a run has been retried and dead-lettered more than once.
            byExecutionId.TryAdd(executionId, new DeadLetterInfo(row.DeadLetterError, row.RetryCount));
        }

        return DeadLetterLookup.From(byExecutionId, horizon);
    }

    /// <summary>
    ///     Start of the dead-letter window, floored so an absurdly wide <see cref="ScheduleDiagnosticsOptions.DeadLetterLookback"/>
    ///     cannot underflow. Internal, and shared with <see cref="ScheduleDeliveryActions"/> — both need exactly the
    ///     same window over the same clock to resolve the same dead letter, and there must be only one definition
    ///     of what that window is.
    /// </summary>
    /// <param name="lookback">How far back the window reaches, from <see cref="ScheduleDiagnosticsOptions.DeadLetterLookback"/>.</param>
    /// <param name="now">The current instant, from the injected <see cref="TimeProvider"/>.</param>
    internal static DateTimeOffset WindowStart(TimeSpan lookback, DateTime now) =>
        lookback > TimeSpan.Zero && now - DateTime.UnixEpoch > lookback
            ? new DateTimeOffset(now - lookback, TimeSpan.Zero)
            : DateTimeOffset.UnixEpoch;

    /// <summary>
    ///     The schedule's current state, which outranks what its last run did. A pure function of its arguments:
    ///     <see cref="RunVerdict.DeliveryUnknown"/> is deliberately not produced here, because it is a statement
    ///     about the read rather than about the run. <see cref="ApplyDeliveryConfidence"/> applies it.
    /// </summary>
    /// <param name="schedule">The schedule this run belongs to.</param>
    /// <param name="execution">The latest run, or null if the sweeper never claimed an occurrence.</param>
    /// <param name="deadLetter">That execution's dead letter, or null if there is none.</param>
    /// <param name="now">The current instant, from the injected <see cref="TimeProvider"/>.</param>
    /// <returns>The verdict.</returns>
    /// <remarks>
    ///     <para>
    ///     <b>The precedence is load-bearing, and it is a product decision rather than an obvious one:</b>
    ///     </para>
    ///     <code>
    ///     Disabled  &gt;  { Failed, Stranded, Undelivered }  &gt;  Overdue  &gt;  { Delivered, DeliveryUnknown, Running, NotYetDue }
    ///     </code>
    ///     <para>
    ///     <b>The rule behind the ordering: <see cref="RunVerdict.Overdue"/> outranks another verdict only when
    ///     that verdict would otherwise read as HEALTHY.</b> The defect it exists to fix is "the page says
    ///     healthy when it is not" — yesterday's <see cref="RunVerdict.Delivered"/> masking a digest that never
    ///     fired today. That reasoning does not extend to an alarm: once the row is already an alarm the
    ///     operator is going to look, and the more specific alarm should win.
    ///     <see cref="RunVerdict.Stranded"/> names a stuck run and the step it is stuck at, and
    ///     <see cref="RunVerdict.Failed"/> carries the error text; <see cref="RunVerdict.Overdue"/> says only
    ///     that nothing ran, so promoting it over either would trade detail for vagueness.
    ///     </para>
    ///     <para>
    ///     Because <see cref="ScheduledRun.NextRunAt"/> advances at claim time, in practice
    ///     <see cref="RunVerdict.Overdue"/> can only ever displace <see cref="RunVerdict.Delivered"/> and
    ///     <see cref="RunVerdict.DeliveryUnknown"/> — the two that read healthy. That is the whole intent,
    ///     stated here directly rather than left to fall out of the ordering.
    ///     </para>
    ///     <para>
    ///     <b>Disabled first,</b> because <see cref="ScheduleSweeperService"/> selects on
    ///     <c>Enabled &amp;&amp; NextRunAt &lt;= now</c>, so a switched-off schedule's
    ///     <see cref="ScheduledRun.NextRunAt"/> is never advanced and slides further into the past every day.
    ///     Ranked any lower, every disabled schedule would read <see cref="RunVerdict.Overdue"/> and send an
    ///     operator hunting a sweeper that is working perfectly.
    ///     </para>
    ///     <para>
    ///     <b>An overdue next occurrence before the last run's outcome,</b> because
    ///     <see cref="ScheduledRunStore"/> calls <see cref="ScheduledRun.AdvanceTo"/> at CLAIM time, inside the
    ///     same transaction that enqueues the run. So <c>NextRunAt &lt;= now</c> means precisely "nothing has
    ///     claimed the next occurrence" and can never mask a run that is legitimately in flight — claiming moves
    ///     it into the future first. Ranked below the execution, the common failure would be invisible: the
    ///     sweeper dies overnight, yesterday's run still reports <see cref="RunVerdict.Delivered"/>, and a page
    ///     whose whole job is "the digest never arrived" shows a healthy row.
    ///     </para>
    /// </remarks>
    private RunVerdict Classify(ScheduledRun schedule, ScheduledRunExecution? execution, DeadLetterInfo? deadLetter, DateTime now)
    {
        if (!schedule.Enabled)
        {
            return RunVerdict.Disabled;
        }

        var runVerdict = execution is null
            ? RunVerdict.NotYetDue
            : ClassifyExecution(execution, deadLetter, now);

        // An alarm already tells the operator to look, and tells them more than Overdue could. Only a verdict
        // that would otherwise read healthy gets displaced by an unclaimed occurrence.
        if (IsAlarm(runVerdict))
        {
            return runVerdict;
        }

        return schedule.NextRunAt <= now ? RunVerdict.Overdue : runVerdict;
    }

    /// <summary>
    ///     Whether this verdict already tells an operator something is wrong. See <see cref="Classify"/>'s
    ///     remarks: these are the verdicts <see cref="RunVerdict.Overdue"/> must not displace, because each
    ///     names a more specific fault than "nothing ran" does.
    /// </summary>
    /// <param name="verdict">The run's own verdict.</param>
    /// <returns>True if the verdict is an alarm rather than a healthy-looking state.</returns>
    /// <remarks>
    ///     <see cref="RunVerdict.DeliveryUnknown"/> is deliberately NOT an alarm here, on two grounds: it is
    ///     applied after this by <see cref="ApplyDeliveryConfidence"/> and so cannot reach this check anyway,
    ///     and it is a statement that nothing could be determined — strictly less informative than
    ///     <see cref="RunVerdict.Overdue"/>, which at least names a real, observed fact about the schedule.
    /// </remarks>
    private static bool IsAlarm(RunVerdict verdict) =>
        verdict is RunVerdict.Failed or RunVerdict.Stranded or RunVerdict.Undelivered;

    /// <summary>
    ///     What one run itself did, on its own terms and with no reference to what the schedule is doing now.
    ///     The drill-down reports this directly; the overview reaches it only after
    ///     <see cref="Classify"/>'s precedence has passed.
    /// </summary>
    /// <param name="execution">The run.</param>
    /// <param name="deadLetter">Its dead letter, or null if there is none.</param>
    /// <param name="now">The current instant, from the injected <see cref="TimeProvider"/>.</param>
    /// <returns>The verdict.</returns>
    private RunVerdict ClassifyExecution(ScheduledRunExecution execution, DeadLetterInfo? deadLetter, DateTime now) =>
        execution.Step switch
        {
            RunStep.Failed => RunVerdict.Failed,

            // Absence of a DEAD LETTER is what means delivered — not presence of a row. A dispatched row may
            // have been pruned, so "no row found" and "no dead letter found" are different questions, and
            // conflating them would report every old successful run as Undelivered.
            RunStep.Done => deadLetter is null ? RunVerdict.Delivered : RunVerdict.Undelivered,

            _ => now - execution.UpdatedAt > _options.StrandedAfter
                ? RunVerdict.Stranded
                : RunVerdict.Running,
        };

    /// <summary>The overview's diagnosis: the schedule's current state, with its latest run as supporting detail.</summary>
    /// <param name="schedule">The schedule.</param>
    /// <param name="execution">Its latest run, or null if the sweeper never claimed an occurrence.</param>
    /// <param name="deadLetters">The dead letters read for this pass.</param>
    /// <param name="now">The current instant, from the injected <see cref="TimeProvider"/>.</param>
    /// <returns>The diagnosis.</returns>
    /// <remarks>
    ///     A row whose verdict is <see cref="RunVerdict.Disabled"/> or <see cref="RunVerdict.Overdue"/> still
    ///     carries the latest execution's fields. The verdict answers "what is wrong now" while those fields
    ///     answer "what happened last time", and the grid shows both columns.
    /// </remarks>
    private RunDiagnosis DiagnoseCurrentState(ScheduledRun schedule, ScheduledRunExecution? execution, DeadLetterLookup deadLetters, DateTime now)
    {
        var deadLetter = execution is null ? null : deadLetters.Find(execution.Id);
        var verdict = Classify(schedule, execution, deadLetter, now);
        return Build(schedule, execution, deadLetter, ApplyDeliveryConfidence(verdict, execution, deadLetters));
    }

    /// <summary>The drill-down's diagnosis: what this one run did, whatever the schedule is doing now.</summary>
    /// <param name="schedule">The schedule the run belongs to.</param>
    /// <param name="execution">The run.</param>
    /// <param name="deadLetters">The dead letters read for this pass.</param>
    /// <param name="now">The current instant, from the injected <see cref="TimeProvider"/>.</param>
    /// <returns>The diagnosis.</returns>
    private RunDiagnosis DiagnoseHistoricalRun(ScheduledRun schedule, ScheduledRunExecution execution, DeadLetterLookup deadLetters, DateTime now)
    {
        var deadLetter = deadLetters.Find(execution.Id);
        var verdict = ClassifyExecution(execution, deadLetter, now);
        return Build(schedule, execution, deadLetter, ApplyDeliveryConfidence(verdict, execution, deadLetters));
    }

    /// <summary>
    ///     Downgrades a <see cref="RunVerdict.Delivered"/> that the outbox read could not actually confirm.
    ///     The one thing the classifiers cannot know: whether the absence they were handed is an answer or a
    ///     gap. Degrade, never blank — and never claim a delivery this could not confirm.
    /// </summary>
    /// <param name="verdict">The classifier's verdict.</param>
    /// <param name="execution">The run, or null if there is none.</param>
    /// <param name="deadLetters">The dead letters read for this pass.</param>
    /// <returns>The verdict, possibly downgraded to <see cref="RunVerdict.DeliveryUnknown"/>.</returns>
    /// <remarks>
    ///     Gated on the verdict already being <see cref="RunVerdict.Delivered"/>, not merely on the run having
    ///     completed. Widened to every run, an unreachable outbox would erase <see cref="RunVerdict.Failed"/>,
    ///     <see cref="RunVerdict.Stranded"/> and <see cref="RunVerdict.Overdue"/> — the verdicts that actually
    ///     tell an operator what to do — and replace them all with a shrug. Only the delivery half is unknown,
    ///     so only the delivery half degrades.
    /// </remarks>
    private static RunVerdict ApplyDeliveryConfidence(RunVerdict verdict, ScheduledRunExecution? execution, DeadLetterLookup deadLetters) =>
        verdict == RunVerdict.Delivered && execution is not null && !deadLetters.CanConfirm(execution.CreatedAt)
            ? RunVerdict.DeliveryUnknown
            : verdict;

    /// <summary>Assembles the reported record once the verdict is settled.</summary>
    /// <param name="schedule">The schedule.</param>
    /// <param name="execution">The run, or null if the sweeper never claimed an occurrence.</param>
    /// <param name="deadLetter">The run's dead letter, or null.</param>
    /// <param name="verdict">The settled verdict.</param>
    /// <returns>The diagnosis.</returns>
    private static RunDiagnosis Build(ScheduledRun schedule, ScheduledRunExecution? execution, DeadLetterInfo? deadLetter, RunVerdict verdict)
    {
        if (execution is null)
        {
            // No execution row means no execution timestamps, and no scout/writer output either. Both
            // timestamps mirror the occurrence rather than defaulting, so the page never renders 0001-01-01
            // and reads it as data.
            return new RunDiagnosis(
                verdict, schedule.Id, schedule.Name, schedule.NextRunAt,
                (int)RunStep.Pending, null, null, 0, null, null,
                schedule.NextRunAt, schedule.NextRunAt, Guid.Empty,
                schedule.Enabled, schedule.NextRunAt, null, null);
        }

        // Findings and Digest are already loaded on execution — GetOverviewAsync and GetRunHistoryAsync both
        // fetch full ScheduledRunExecution rows, not a projection — so reading them here adds no query.
        return new RunDiagnosis(
            verdict, schedule.Id, schedule.Name, execution.OccurrenceAt,
            (int)execution.Step, (int?)execution.FailedAtStep, execution.LastError, execution.Attempts,
            deadLetter?.Error, deadLetter?.RetryCount, execution.CreatedAt, execution.UpdatedAt, execution.Id,
            schedule.Enabled, schedule.NextRunAt, execution.Findings, execution.Digest);
    }

    /// <summary>The columns of a dead-lettered row this type needs, projected in the database.</summary>
    /// <param name="Payload">The opaque serialized <see cref="ChannelMessageQueued"/>.</param>
    /// <param name="DeadLetterError">Why delivery was abandoned. The half <c>IOutboxDashboardStore</c> cannot give.</param>
    /// <param name="RetryCount">How many attempts were made before it was abandoned.</param>
    /// <param name="CreatedAt">When the message was queued. Only used to find how far back a capped scan reaches.</param>
    private sealed record DeadLetterRow(byte[] Payload, string? DeadLetterError, int RetryCount, DateTimeOffset CreatedAt);

    /// <summary>What a dead letter contributes to a diagnosis.</summary>
    /// <param name="Error">Why delivery was abandoned.</param>
    /// <param name="RetryCount">How many attempts were made.</param>
    private sealed record DeadLetterInfo(string? Error, int RetryCount);

    /// <summary>
    ///     The result of the dead-letter read, and — just as importantly — how much of it can be trusted.
    ///     <see cref="Available"/> separates "the outbox says none died" from "the outbox could not be asked",
    ///     and <see cref="Horizon"/> separates both from "the outbox was only asked about recent rows". All
    ///     three look identical as a missing dictionary entry and must never collapse into one.
    /// </summary>
    /// <param name="Available">Whether the read succeeded at all.</param>
    /// <param name="ByExecutionId">The dead letters found, keyed by the execution that produced the message.</param>
    /// <param name="Horizon">
    ///     The oldest instant this scan reached, when the row cap truncated it; null when the scan was complete
    ///     within its window. An absence older than this is not evidence of anything.
    /// </param>
    private sealed record DeadLetterLookup(bool Available, IReadOnlyDictionary<Guid, DeadLetterInfo> ByExecutionId, DateTime? Horizon)
    {
        /// <summary>The read failed. Completed runs degrade to <see cref="RunVerdict.DeliveryUnknown"/>.</summary>
        public static readonly DeadLetterLookup Unavailable = new(false, ReadOnlyDictionary<Guid, DeadLetterInfo>.Empty, null);

        /// <summary>The read succeeded and found the given dead letters.</summary>
        /// <param name="byExecutionId">The dead letters found.</param>
        /// <param name="horizon">How far back the scan reached if it was capped; null if it was complete.</param>
        /// <returns>An available lookup.</returns>
        public static DeadLetterLookup From(IReadOnlyDictionary<Guid, DeadLetterInfo> byExecutionId, DateTime? horizon) => new(true, byExecutionId, horizon);

        /// <summary>This execution's dead letter, or null if it has none.</summary>
        /// <param name="executionId">The execution to look up.</param>
        /// <returns>The dead letter, or null.</returns>
        public DeadLetterInfo? Find(Guid executionId) => ByExecutionId.GetValueOrDefault(executionId);

        /// <summary>
        ///     Whether "no dead letter found" is actually evidence of delivery for a run created at
        ///     <paramref name="executionCreatedAt"/>.
        /// </summary>
        /// <param name="executionCreatedAt">When the execution row was created.</param>
        /// <returns>True only if this scan genuinely covered that run's message.</returns>
        /// <remarks>
        ///     Compares against the execution's CREATION rather than its completion, which is the conservative
        ///     side: the message is queued at the deliver step, so it is always newer than the row's
        ///     <see cref="ScheduledRunExecution.CreatedAt"/>. A run that started after the horizon therefore had
        ///     its message inside the scanned set for certain, and one that started before it may not have.
        /// </remarks>
        public bool CanConfirm(DateTime executionCreatedAt) =>
            Available && (Horizon is null || executionCreatedAt >= Horizon.Value);
    }
}
