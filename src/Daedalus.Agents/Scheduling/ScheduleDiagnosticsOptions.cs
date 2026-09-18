namespace Daedalus.Agents.Scheduling;

/// <summary>
///     Binds the <c>ScheduleDiagnostics</c> configuration section: the thresholds and bounds
///     <see cref="ScheduleDiagnostics"/> classifies and reads with.
/// </summary>
/// <remarks>
///     Every property is settable with a default rather than <c>required</c>, so a host that never configures the
///     section still gets a working service. A diagnostics page that refused to start because nobody wrote a
///     config entry would be the exact failure mode it exists to report on.
/// </remarks>
public sealed class ScheduleDiagnosticsOptions
{
    /// <summary>The configuration section this binds from.</summary>
    public const string SectionName = "ScheduleDiagnostics";

    /// <summary>
    ///     How long a non-terminal run may go without its <see cref="Domain.Entities.ScheduledRunExecution.UpdatedAt"/>
    ///     advancing before it is reported <see cref="Application.DTOs.Scheduling.RunVerdict.Stranded"/> rather than
    ///     <see cref="Application.DTOs.Scheduling.RunVerdict.Running"/>.
    /// </summary>
    /// <remarks>
    ///     Fifteen minutes because the outbox retries eight times with exponential backoff — roughly two minutes —
    ///     so anything non-terminal for more than a few minutes is stuck rather than slow. Configurable because
    ///     the first surprising workload would invalidate that reasoning.
    /// </remarks>
    public TimeSpan StrandedAfter { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    ///     How far back the dead-letter scan looks. Bounds the one outbox read so it cannot degrade into a full
    ///     table scan as the outbox grows.
    /// </summary>
    /// <remarks>
    ///     Generous on purpose: a dead-lettered row is never pruned by the worker, and a completed run whose dead
    ///     letter fell outside this window would be reported
    ///     <see cref="Application.DTOs.Scheduling.RunVerdict.Delivered"/> — the misleading direction. Widen this
    ///     before narrowing it.
    /// </remarks>
    public TimeSpan DeadLetterLookback { get; set; } = TimeSpan.FromDays(30);

    /// <summary>The maximum number of dead-lettered rows a single scan deserializes.</summary>
    /// <remarks>
    ///     The payload is an opaque blob, so <c>ExecutionId</c> cannot be filtered in SQL and the survivors of the
    ///     type-and-status narrowing must be deserialized in memory. Newest first, so the cap drops the oldest
    ///     failures rather than the ones an operator is looking at.
    /// </remarks>
    public int DeadLetterScanLimit { get; set; } = 500;

    /// <summary>The ceiling applied to <see cref="Application.Abstractions.IScheduleDiagnostics.GetRunHistoryAsync"/>'s <c>take</c>.</summary>
    public int MaxRunHistory { get; set; } = 200;
}
