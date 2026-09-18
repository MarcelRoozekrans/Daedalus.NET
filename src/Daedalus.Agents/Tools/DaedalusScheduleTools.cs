using System.ComponentModel;
using System.Globalization;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs.Scheduling;
using Daedalus.Domain.Entities;
using Thalos;

namespace Daedalus.Agents.Tools;

/// <summary>
///     Read-only schedule diagnostics exposed to Thalos agents: <c>daedalus__list_schedules</c> and
///     <c>daedalus__why_did_a_run_fail</c>. Registered under the same tool-source name as
///     <see cref="DaedalusKnowledgeTools"/> (<c>daedalus</c>), not a second source.
/// </summary>
/// <remarks>
///     <para>
///     <b>No create, no cancel, no enable/disable.</b> Both methods call straight through to
///     <see cref="IScheduleDiagnostics"/> and only shape its answer into text — the same rule
///     <c>SchedulesController</c> follows, and for the same reason: every classification decision belongs to the
///     service, where it is tested once. An agent that cannot schedule work cannot schedule work for itself,
///     which the roadmap deliberately treats as a privilege surface for a later phase rather than a side effect
///     of a diagnostics page.
///     </para>
///     <para>
///     <b>Why text, not JSON.</b> A Radzen grid has colour and layout to carry a verdict; a tool response is only
///     words a model reads once and reasons over. Each rendered line names the verdict, the step reached, the
///     step a failure happened at (when one did) and the error text, so a caller can state where a run died
///     without re-deriving it from raw enum numbers.
///     </para>
/// </remarks>
/// <param name="diagnostics">
///     The one seam both this tool and <c>SchedulesController</c> read through, so a page and an agent cannot
///     give an operator different answers about the same run.
/// </param>
[ThalosToolType]
public sealed class DaedalusScheduleTools(IScheduleDiagnostics diagnostics)
{
    /// <summary>Lists every schedule and the verdict on its current state.</summary>
    [ThalosTool("list_schedules")]
    [Description(
        "List every schedule Daedalus knows about, with its current state: delivered, running, not yet due, " +
        "overdue, stranded, delivery unknown, failed, undelivered, or disabled — the same verdict the schedules " +
        "page shows. Use this first to find a schedule's id, then pass it to why_did_a_run_fail for the detail " +
        "behind an alarming verdict.")]
    public async Task<string> ListSchedules(CancellationToken ct = default)
    {
        var overview = await diagnostics.GetOverviewAsync(ct);
        return overview.Count == 0
            ? "No schedules are configured."
            : string.Join('\n', overview.Select(RenderOverviewLine));
    }

    /// <summary>Explains the most recent runs of one schedule, most recent first.</summary>
    [ThalosTool("why_did_a_run_fail")]
    [Description(
        "Explain what happened to a schedule's most recent runs, most recent first: whether each was delivered, " +
        "is still running, failed, was stranded mid-flight, completed but never delivered, or never ran at all " +
        "— and why, including the step it died at and the error text when there is one. Pass the schedule id " +
        "from list_schedules.")]
    public async Task<string> WhyDidARunFail(
        [Description("The schedule's id, as returned by list_schedules.")] Guid scheduleId,
        [Description("How many of the schedule's most recent runs to inspect (default 5).")] int take = 5,
        CancellationToken ct = default)
    {
        var history = await diagnostics.GetRunHistoryAsync(scheduleId, take, ct);
        return history.Count == 0
            ? $"Schedule {scheduleId} has no recorded runs, or no such schedule exists."
            : string.Join('\n', history.Select(RenderRunLine));
    }

    private static string RenderOverviewLine(RunDiagnosis d) =>
        $"- {d.ScheduleName} (id={d.ScheduleId}): Verdict={d.Verdict}. {Narrate(d)} " +
        $"Enabled={d.Enabled}. NextRunAtUtc={FormatNullable(d.NextRunAtUtc)}.";

    private static string RenderRunLine(RunDiagnosis d) =>
        $"- Occurrence {FormatUtc(d.OccurrenceAtUtc)} (execution={d.ExecutionId}): Verdict={d.Verdict}. {Narrate(d)} " +
        $"Attempts={d.Attempts}.";

    /// <summary>
    ///     The sentence a model needs to state where the run died: which step it reached or failed at, and the
    ///     error or dead-letter reason when the verdict carries one. Mirrors <c>RunVerdict</c>'s own doc comments
    ///     so the tool's wording never drifts from the service's definition of each verdict.
    /// </summary>
    private static string Narrate(RunDiagnosis d) => d.Verdict switch
    {
        RunVerdict.Delivered => "The digest was delivered.",
        RunVerdict.Running => $"Still running; the {(RunStep)d.StepReached} step is queued or executing.",
        RunVerdict.NotYetDue => "This occurrence has not become due yet.",
        RunVerdict.Overdue => "Overdue: nothing has claimed this occurrence though it should have run by now.",
        RunVerdict.Stranded => $"Stranded at the {(RunStep)d.StepReached} step; it stopped advancing without completing or failing.",
        RunVerdict.DeliveryUnknown => "The run completed, but delivery could not be confirmed: either the outbox could not be read, or the dead-letter scan did not reach back far enough to check.",
        RunVerdict.Failed => $"Failed at the {FailedStepName(d)} step: {d.LastError ?? "no error was recorded"}.",
        RunVerdict.Undelivered => $"The run completed, but the message was dead-lettered: " +
                                   $"{d.DeadLetterError ?? "no reason was recorded"}{RetrySuffix(d.RetryCount)}",
        RunVerdict.Disabled => "This schedule is disabled and will not run again until it is re-enabled.",
        _ => "No verdict could be determined.",
    };

    private static string FailedStepName(RunDiagnosis d) =>
        (d.FailedAtStep is { } step ? (RunStep)step : (RunStep)d.StepReached).ToString();

    private static string RetrySuffix(int? retryCount) =>
        retryCount is { } count ? $" after {count} attempt(s)." : ".";

    private static string FormatUtc(DateTime dt) => dt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string FormatNullable(DateTime? dt) => dt is { } value ? FormatUtc(value) : "unknown";
}
