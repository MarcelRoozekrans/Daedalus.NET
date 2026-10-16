using Daedalus.Application.DTOs.Scheduling;
using Daedalus.Web.Auth;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Flux;

namespace Daedalus.Web.State.Schedules;

/// <summary>
///     The Schedules page's I/O: the overview, a schedule's run history and a resend. An expected failure is an action,
///     or a refused outcome, never an exception. A caller already gone dispatches nothing, and a read whose caller went
///     while it was in flight stores nothing. A session being renewed stores nothing and logs nothing. Every other failure
///     is logged as a warning, with its text only.
/// </summary>
public sealed partial class SchedulesEffects(
    ApiClient api, IDispatcher dispatcher, IStore<SchedulesState> schedules, ILogger<SchedulesEffects> logger)
{
    /// <summary>How many runs a schedule's history reads, newest first.</summary>
    public const int HistoryTake = 20;

    /// <summary>Reads the overview, showing the spinner meanwhile: on open, and on Retry.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return;
        }

        // Read and dispatched with no await between, so the next read takes the number after this one's.
        var sequence = schedules.Value.Sequence + 1;
        await dispatcher.DispatchAsync(new SchedulesLoadStarted(sequence));
        var result = await api.GetScheduleOverviewAsync(ct);
        if (ct.IsCancellationRequested)
        {
            return;
        }

        if (result.IsSuccess)
        {
            await dispatcher.DispatchAsync(new SchedulesLoaded(sequence, result.Value));
        }
        else if (!SessionExpiry.IsSigningIn(result.Error))
        {
            LogOverviewFailed(logger, result.Error);
            await dispatcher.DispatchAsync(new SchedulesLoadFailed(sequence, result.Error));
        }
    }

    /// <summary>Reads schedule <paramref name="scheduleId"/>'s run history unless the page holds it: a row expanded.</summary>
    public Task EnsureHistoryAsync(Guid scheduleId, CancellationToken ct) =>
        schedules.Value.Histories.ContainsKey(scheduleId) ? Task.CompletedTask : LoadHistoryAsync(scheduleId, ct);

    /// <summary>Reads schedule <paramref name="scheduleId"/>'s run history again: the history's Retry.</summary>
    public Task RetryHistoryAsync(Guid scheduleId, CancellationToken ct) => LoadHistoryAsync(scheduleId, ct);

    /// <summary>
    ///     Asks the API to requeue <paramref name="run"/>'s dead-lettered message, and returns the outcome: requeued, or
    ///     refused with the API's reason. A session being renewed, or a caller gone, is no outcome,
    ///     <see langword="null"/>. Nothing is stored: the verdict is never rewritten until the overview is read again, as
    ///     the page must never claim a delivery it has not seen.
    /// </summary>
    public async Task<ScheduleResend?> ResendAsync(RunDiagnosis run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        var result = await api.ResendScheduleRunAsync(run.ScheduleId, run.ExecutionId, ct);
        if (result.IsSuccess)
        {
            return ScheduleResend.Requeued;
        }

        if (SessionExpiry.IsSigningIn(result.Error) || ct.IsCancellationRequested)
        {
            return null;
        }

        LogResendRefused(logger, result.Error);
        return new ScheduleResend(result.Error);
    }

    private async Task LoadHistoryAsync(Guid scheduleId, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return;
        }

        var sequence = schedules.Value.Sequence + 1;
        await dispatcher.DispatchAsync(new ScheduleHistoryStarted(scheduleId, sequence));
        var result = await api.GetScheduleRunHistoryAsync(scheduleId, HistoryTake, ct);
        if (ct.IsCancellationRequested)
        {
            return;
        }

        if (result.IsSuccess)
        {
            await dispatcher.DispatchAsync(new ScheduleHistoryLoaded(scheduleId, sequence, result.Value));
        }
        else if (!SessionExpiry.IsSigningIn(result.Error))
        {
            LogHistoryFailed(logger, result.Error);
            await dispatcher.DispatchAsync(new ScheduleHistoryFailed(scheduleId, sequence, result.Error));
        }
    }

    [LoggerMessage(EventId = 3600, Level = LogLevel.Warning, Message = "The schedules could not be loaded: {Error}")]
    private static partial void LogOverviewFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 3601, Level = LogLevel.Warning, Message = "A schedule's run history could not be loaded: {Error}")]
    private static partial void LogHistoryFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 3602, Level = LogLevel.Warning, Message = "A resend was refused: {Reason}")]
    private static partial void LogResendRefused(ILogger logger, string reason);
}
