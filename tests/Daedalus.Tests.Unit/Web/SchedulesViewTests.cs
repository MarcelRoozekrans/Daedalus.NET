using Daedalus.Application.DTOs.Scheduling;
using Daedalus.Web.State.Schedules;

namespace Daedalus.Tests.Unit.Web;

/// <summary>What the Schedules page shows in place of its overview.</summary>
public sealed class SchedulesViewTests
{
    private static readonly RunDiagnosis Run = new(
        RunVerdict.Delivered, Guid.NewGuid(), "Morning digest", DateTime.UnixEpoch, 4, null, null, 1, null, null,
        DateTime.UnixEpoch, DateTime.UnixEpoch, Guid.NewGuid(), true, null, null, null);

    private static SchedulesState Initial => SchedulesState.Initial(null!);

    /// <summary>
    ///     Before the overview lands, and after a read that ended with the session being renewed, the spinner shows.
    ///     Red: return Empty for a null Overview; the note shows while loading.
    /// </summary>
    [Fact]
    public void Nothing_read_shows_the_spinner()
    {
        SchedulesView.Overview(Initial).Should().Be(SchedulesShow.Loading);
    }

    /// <summary>
    ///     A failure shows the failure, even over an overview held from before.
    ///     Red: test Overview before Error in Overview; a held overview hides the failure.
    /// </summary>
    [Fact]
    public void A_failure_shows_the_failure()
    {
        SchedulesView.Overview(Initial with { Error = "boom" }).Should().Be(SchedulesShow.Error);
        SchedulesView.Overview(Initial with { Overview = [Run], Error = "boom" }).Should().Be(SchedulesShow.Error);
    }

    /// <summary>
    ///     No schedule shows the note; schedules show the overview.
    ///     Red: drop the Empty arm in Overview; no schedule shows an empty grid.
    ///     Red: return Empty for any overview; the schedules never show.
    /// </summary>
    [Fact]
    public void An_overview_shows_its_schedules_or_the_note()
    {
        SchedulesView.Overview(Initial with { Overview = [] }).Should().Be(SchedulesShow.Empty);
        SchedulesView.Overview(Initial with { Overview = [Run] }).Should().Be(SchedulesShow.Loaded);
    }
}
