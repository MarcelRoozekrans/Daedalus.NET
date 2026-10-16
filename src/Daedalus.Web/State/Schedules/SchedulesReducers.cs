using ZeroAlloc.Flux;

namespace Daedalus.Web.State.Schedules;

/// <summary>
///     The Schedules reducers. Pure: no I/O, no clock. The overview, and each schedule's history, keep an answer only to
///     their newest read; see <see cref="SchedulesState"/>.
/// </summary>
public static class SchedulesReducers
{
    /// <summary>
    ///     Marks the overview's newest read and clears what the last one showed, so the page shows its spinner, as it always
    ///     did, and a read that ends with the session being renewed keeps it. A start seen out of order never moves the
    ///     sequence back.
    /// </summary>
    [Reducer]
    public static SchedulesState On(SchedulesState s, SchedulesLoadStarted a) =>
        s with { Sequence = Math.Max(s.Sequence, a.Sequence), OverviewSequence = Math.Max(s.OverviewSequence, a.Sequence), Overview = null, Error = null };

    [Reducer]
    public static SchedulesState On(SchedulesState s, SchedulesLoaded a) =>
        a.Sequence == s.OverviewSequence ? s with { Overview = a.Overview } : s;

    [Reducer]
    public static SchedulesState On(SchedulesState s, SchedulesLoadFailed a) =>
        a.Sequence == s.OverviewSequence ? s with { Error = a.Error } : s;

    /// <summary>The schedule's history shows "Loading run history…" until its newest read lands; a selected run goes.</summary>
    [Reducer]
    public static SchedulesState On(SchedulesState s, ScheduleHistoryStarted a) =>
        s with
        {
            Sequence = Math.Max(s.Sequence, a.Sequence),
            Histories = s.Histories.SetItem(a.ScheduleId, new ScheduleHistory(null, null, null, a.Sequence)),
        };

    [Reducer]
    public static SchedulesState On(SchedulesState s, ScheduleHistoryLoaded a) =>
        Awaited(s, a.ScheduleId, a.Sequence) is { } history
            ? s with { Histories = s.Histories.SetItem(a.ScheduleId, history with { Runs = a.Runs }) }
            : s;

    [Reducer]
    public static SchedulesState On(SchedulesState s, ScheduleHistoryFailed a) =>
        Awaited(s, a.ScheduleId, a.Sequence) is { } history
            ? s with { Histories = s.Histories.SetItem(a.ScheduleId, history with { Error = a.Error }) }
            : s;

    /// <summary>Selects a run in a history the page holds; a schedule with none is left alone, as the page always did.</summary>
    [Reducer]
    public static SchedulesState On(SchedulesState s, ScheduleRunSelected a) =>
        s.Histories.TryGetValue(a.ScheduleId, out var history)
            ? s with { Histories = s.Histories.SetItem(a.ScheduleId, history with { Selected = a.Run }) }
            : s;

    /// <summary>Clears the page's fields and keeps the sequence; see <see cref="SchedulesState"/>.</summary>
    [Reducer]
    public static SchedulesState On(SchedulesState s, SchedulesClosed a) => SchedulesState.Empty with { Sequence = s.Sequence };

    /// <summary>The history of <paramref name="scheduleId"/> when read <paramref name="sequence"/> is its newest.</summary>
    private static ScheduleHistory? Awaited(SchedulesState s, Guid scheduleId, long sequence) =>
        s.Histories.TryGetValue(scheduleId, out var history) && history.Sequence == sequence ? history : null;
}
