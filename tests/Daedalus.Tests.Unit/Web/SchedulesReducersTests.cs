using Daedalus.Application.DTOs.Scheduling;
using Daedalus.Web.State.Schedules;

namespace Daedalus.Tests.Unit.Web;

/// <summary>The Schedules reducers: the overview, each schedule's history, the selected run, the sequence guards and the close.</summary>
public sealed class SchedulesReducersTests
{
    private static readonly Guid ScheduleId = Guid.NewGuid();
    private static readonly Guid OtherId = Guid.NewGuid();

    private static readonly RunDiagnosis Run = new(
        RunVerdict.Delivered, ScheduleId, "Morning digest", DateTime.UnixEpoch, 4, null, null, 1, null, null,
        DateTime.UnixEpoch, DateTime.UnixEpoch, Guid.NewGuid(), true, null, null, null);

    private static SchedulesState Initial => SchedulesState.Initial(null!);

    private static SchedulesState Expanded(long sequence = 1) => SchedulesReducers.On(Initial, new ScheduleHistoryStarted(ScheduleId, sequence));

    /// <summary>
    ///     An overview start clears what the last read showed, so the spinner shows, and takes its number.
    ///     Red: keep Overview in the SchedulesLoadStarted reducer; the old overview is still there.
    ///     Red: keep Error in the SchedulesLoadStarted reducer; the old error is still there.
    ///     Red: leave OverviewSequence unset in the SchedulesLoadStarted reducer; it stays 0.
    ///     Red: set Sequence to the action's in the SchedulesLoadStarted reducer; an out-of-order start moves it back to 2.
    /// </summary>
    [Fact]
    public void An_overview_start_clears_the_last_read_and_takes_its_number()
    {
        var shown = Initial with { Overview = [Run], Error = "boom", Sequence = 3 };

        var started = SchedulesReducers.On(shown, new SchedulesLoadStarted(4));

        started.Overview.Should().BeNull();
        started.Error.Should().BeNull();
        started.OverviewSequence.Should().Be(4);
        SchedulesReducers.On(started, new SchedulesLoadStarted(2)).Sequence.Should().Be(4);
    }

    /// <summary>
    ///     The overview lands, and a failure is the error.
    ///     Red: drop Overview in the SchedulesLoaded reducer; the overview stays null.
    ///     Red: drop Error in the SchedulesLoadFailed reducer; the error stays null.
    /// </summary>
    [Fact]
    public void The_overview_or_its_failure_lands()
    {
        var started = SchedulesReducers.On(Initial, new SchedulesLoadStarted(1));

        SchedulesReducers.On(started, new SchedulesLoaded(1, [Run])).Overview.Should().Equal(Run);
        SchedulesReducers.On(started, new SchedulesLoadFailed(1, "API error 500")).Error.Should().Be("API error 500");
    }

    /// <summary>
    ///     An answer to an older overview read is dropped, and so is its failure.
    ///     Red: drop the sequence check in the SchedulesLoaded reducer; the older overview lands.
    ///     Red: drop the sequence check in the SchedulesLoadFailed reducer; the older failure lands.
    /// </summary>
    [Fact]
    public void An_older_overview_is_dropped()
    {
        var started = SchedulesReducers.On(Initial, new SchedulesLoadStarted(2));

        SchedulesReducers.On(started, new SchedulesLoaded(1, [Run])).Overview.Should().BeNull();
        SchedulesReducers.On(started, new SchedulesLoadFailed(1, "boom")).Error.Should().BeNull();
    }

    /// <summary>
    ///     A history start shows the schedule loading, takes its number, and leaves other schedules alone; a restart drops
    ///     the run selected.
    ///     Red: drop the entry in the ScheduleHistoryStarted reducer; the schedule has no history.
    ///     Red: keep the old entry's Selected in the ScheduleHistoryStarted reducer; the run stays selected.
    ///     Red: leave Sequence unset in the ScheduleHistoryStarted reducer; the sequence stays 0.
    /// </summary>
    [Fact]
    public void A_history_start_shows_the_schedule_loading()
    {
        var selected = SchedulesReducers.On(
            SchedulesReducers.On(Expanded(), new ScheduleHistoryLoaded(ScheduleId, 1, [Run])), new ScheduleRunSelected(ScheduleId, Run));

        var restarted = SchedulesReducers.On(selected, new ScheduleHistoryStarted(ScheduleId, 2));

        restarted.Histories[ScheduleId].Loading.Should().BeTrue();
        restarted.Histories[ScheduleId].Selected.Should().BeNull();
        restarted.Sequence.Should().Be(2);
        restarted.Histories.Should().NotContainKey(OtherId);
    }

    /// <summary>
    ///     A history lands as the runs, and a failure as the error.
    ///     Red: drop Runs in the ScheduleHistoryLoaded reducer; the history stays loading.
    ///     Red: drop Error in the ScheduleHistoryFailed reducer; the history stays loading.
    /// </summary>
    [Fact]
    public void A_history_or_its_failure_lands()
    {
        var loaded = SchedulesReducers.On(Expanded(), new ScheduleHistoryLoaded(ScheduleId, 1, [Run]));
        var failed = SchedulesReducers.On(Expanded(), new ScheduleHistoryFailed(ScheduleId, 1, "API error 500"));

        loaded.Histories[ScheduleId].Runs.Should().Equal(Run);
        failed.Histories[ScheduleId].Error.Should().Be("API error 500");
        failed.Histories[ScheduleId].Loading.Should().BeFalse();
    }

    /// <summary>
    ///     An answer to an older history read, such as before a Retry, is dropped, and one for a schedule the page no longer
    ///     holds is too.
    ///     Red: drop the sequence check in Awaited; the older history lands.
    ///     Red: add the history in the ScheduleHistoryLoaded reducer when the page holds none; the closed page holds one.
    /// </summary>
    [Fact]
    public void An_older_history_is_dropped()
    {
        var retried = SchedulesReducers.On(Expanded(1), new ScheduleHistoryStarted(ScheduleId, 2));

        SchedulesReducers.On(retried, new ScheduleHistoryLoaded(ScheduleId, 1, [Run])).Histories[ScheduleId].Loading.Should().BeTrue();
        SchedulesReducers.On(retried, new ScheduleHistoryFailed(ScheduleId, 1, "boom")).Histories[ScheduleId].Loading.Should().BeTrue();
        SchedulesReducers.On(Initial, new ScheduleHistoryLoaded(ScheduleId, 0, [Run])).Histories.Should().BeEmpty();
    }

    /// <summary>
    ///     A run selected in a history shows its detail; one in a history the page does not hold is ignored.
    ///     Red: drop Selected in the ScheduleRunSelected reducer; no run is selected.
    ///     Red: add a history in the ScheduleRunSelected reducer when the page holds none; the other schedule has one.
    /// </summary>
    [Fact]
    public void A_selected_run_shows_in_its_history()
    {
        var loaded = SchedulesReducers.On(Expanded(), new ScheduleHistoryLoaded(ScheduleId, 1, [Run]));

        SchedulesReducers.On(loaded, new ScheduleRunSelected(ScheduleId, Run)).Histories[ScheduleId].Selected.Should().Be(Run);
        SchedulesReducers.On(loaded, new ScheduleRunSelected(OtherId, Run)).Histories.Should().NotContainKey(OtherId);
    }

    /// <summary>
    ///     The close clears what the page read and keeps the sequence.
    ///     Red: return the state unchanged in the SchedulesClosed reducer; the overview is still there.
    ///     Red: return SchedulesState.Empty in the SchedulesClosed reducer; the sequence goes back to 0.
    /// </summary>
    [Fact]
    public void The_close_clears_the_page_and_keeps_the_sequence()
    {
        var read = SchedulesReducers.On(Expanded(3), new SchedulesLoadStarted(4)) with { Overview = [Run] };

        var closed = SchedulesReducers.On(read, new SchedulesClosed());

        closed.Overview.Should().BeNull();
        closed.Histories.Should().BeEmpty();
        closed.Sequence.Should().Be(4);
    }
}
