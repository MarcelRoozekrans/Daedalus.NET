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
    ///     Active means <see cref="ScheduledRun.Enabled"/>. A disabled schedule is deliberately absent rather
    ///     than reported <see cref="RunVerdict.Overdue"/>: it is not overdue, it is switched off, and
    ///     <see cref="RunVerdict"/> has no member that says so.
    /// </remarks>
    public async ValueTask<IReadOnlyList<RunDiagnosis>> GetOverviewAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Read 1 of 3.
        var schedules = await db.ScheduledRuns
            .AsNoTracking()
            .Where(s => s.Enabled)
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

        return schedules.ConvertAll(s => Diagnose(s, byScheduleId.GetValueOrDefault(s.Id), deadLetters, now));
    }

    /// <inheritdoc/>
    /// <remarks>
    ///     Executions only. A schedule that has never been claimed has no history, and this returns an empty list
    ///     rather than synthesising a row: the contract is "the most recent executions", and a caller wanting the
    ///     <see cref="RunVerdict.NotYetDue"/> or <see cref="RunVerdict.Overdue"/> answer gets it from
    ///     <see cref="GetOverviewAsync"/>, which is where it belongs.
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

        return executions.ConvertAll(e => Diagnose(schedule, e, deadLetters, now));
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
        var since = WindowStart(now);

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
                .Select(m => new DeadLetterRow(m.Payload, m.DeadLetterError, m.RetryCount))
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

        return DeadLetterLookup.From(byExecutionId);
    }

    /// <summary>Start of the dead-letter window, floored so an absurdly wide <see cref="ScheduleDiagnosticsOptions.DeadLetterLookback"/> cannot underflow.</summary>
    private DateTimeOffset WindowStart(DateTime now)
    {
        var lookback = _options.DeadLetterLookback;
        return lookback > TimeSpan.Zero && now - DateTime.UnixEpoch > lookback
            ? new DateTimeOffset(now - lookback, TimeSpan.Zero)
            : DateTimeOffset.UnixEpoch;
    }

    /// <summary>
    ///     Where this run died, given everything that was read about it. A pure function of its arguments:
    ///     <see cref="RunVerdict.DeliveryUnknown"/> is deliberately not produced here, because it is a statement
    ///     about the read rather than about the run. <see cref="Diagnose"/> applies it.
    /// </summary>
    /// <param name="schedule">The schedule this run belongs to.</param>
    /// <param name="execution">The run, or null if the sweeper never claimed the occurrence.</param>
    /// <param name="deadLetter">This execution's dead letter, or null if there is none.</param>
    /// <param name="now">The current instant, from the injected <see cref="TimeProvider"/>.</param>
    /// <returns>The verdict.</returns>
    private RunVerdict Classify(ScheduledRun schedule, ScheduledRunExecution? execution, DeadLetterInfo? deadLetter, DateTime now)
    {
        if (execution is null)
        {
            return schedule.NextRunAt > now ? RunVerdict.NotYetDue : RunVerdict.Overdue;
        }

        return execution.Step switch
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
    }

    /// <summary>Builds the reported diagnosis for one run.</summary>
    /// <param name="schedule">The schedule this run belongs to.</param>
    /// <param name="execution">The run, or null if the sweeper never claimed the occurrence.</param>
    /// <param name="deadLetters">The dead letters read for this pass, or <see cref="DeadLetterLookup.Unavailable"/>.</param>
    /// <param name="now">The current instant, from the injected <see cref="TimeProvider"/>.</param>
    /// <returns>The diagnosis.</returns>
    private RunDiagnosis Diagnose(ScheduledRun schedule, ScheduledRunExecution? execution, DeadLetterLookup deadLetters, DateTime now)
    {
        var deadLetter = execution is null ? null : deadLetters.Find(execution.Id);
        var verdict = Classify(schedule, execution, deadLetter, now);

        // The one thing Classify cannot know: whether the absence it was handed is an answer or a failure.
        // A completed run whose delivery could not be read is DeliveryUnknown, never Delivered.
        if (!deadLetters.Available && execution?.Step == RunStep.Done)
        {
            verdict = RunVerdict.DeliveryUnknown;
        }

        if (execution is null)
        {
            // No execution row means no execution timestamps. Both mirror the occurrence rather than defaulting,
            // so the page never renders 0001-01-01 and reads it as data.
            return new RunDiagnosis(
                verdict, schedule.Id, schedule.Name, schedule.NextRunAt,
                (int)RunStep.Pending, null, null, 0, null, null,
                schedule.NextRunAt, schedule.NextRunAt, Guid.Empty);
        }

        return new RunDiagnosis(
            verdict, schedule.Id, schedule.Name, execution.OccurrenceAt,
            (int)execution.Step, (int?)execution.FailedAtStep, execution.LastError, execution.Attempts,
            deadLetter?.Error, deadLetter?.RetryCount, execution.CreatedAt, execution.UpdatedAt, execution.Id);
    }

    /// <summary>The three columns of a dead-lettered row this type needs, projected in the database.</summary>
    /// <param name="Payload">The opaque serialized <see cref="ChannelMessageQueued"/>.</param>
    /// <param name="DeadLetterError">Why delivery was abandoned. The half <c>IOutboxDashboardStore</c> cannot give.</param>
    /// <param name="RetryCount">How many attempts were made before it was abandoned.</param>
    private sealed record DeadLetterRow(byte[] Payload, string? DeadLetterError, int RetryCount);

    /// <summary>What a dead letter contributes to a diagnosis.</summary>
    /// <param name="Error">Why delivery was abandoned.</param>
    /// <param name="RetryCount">How many attempts were made.</param>
    private sealed record DeadLetterInfo(string? Error, int RetryCount);

    /// <summary>
    ///     The result of the dead-letter read. <see cref="Available"/> separates "the outbox says none died" from
    ///     "the outbox could not be asked" — two answers that look identical as an empty dictionary and must
    ///     never collapse into one.
    /// </summary>
    /// <param name="Available">Whether the read succeeded.</param>
    /// <param name="ByExecutionId">The dead letters found, keyed by the execution that produced the message.</param>
    private sealed record DeadLetterLookup(bool Available, IReadOnlyDictionary<Guid, DeadLetterInfo> ByExecutionId)
    {
        /// <summary>The read failed. Completed runs degrade to <see cref="RunVerdict.DeliveryUnknown"/>.</summary>
        public static readonly DeadLetterLookup Unavailable = new(false, ReadOnlyDictionary<Guid, DeadLetterInfo>.Empty);

        /// <summary>The read succeeded and found the given dead letters.</summary>
        /// <param name="byExecutionId">The dead letters found.</param>
        /// <returns>An available lookup.</returns>
        public static DeadLetterLookup From(IReadOnlyDictionary<Guid, DeadLetterInfo> byExecutionId) => new(true, byExecutionId);

        /// <summary>This execution's dead letter, or null if it has none.</summary>
        /// <param name="executionId">The execution to look up.</param>
        /// <returns>The dead letter, or null.</returns>
        public DeadLetterInfo? Find(Guid executionId) => ByExecutionId.GetValueOrDefault(executionId);
    }
}
