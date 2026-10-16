using Daedalus.Application.DTOs;
using Daedalus.Web.State.Projects;

namespace Daedalus.Tests.Unit.Web;

/// <summary>
///     What a projects list renders: the spinner for a component's own load and for a load with nothing yet to show, never
///     for a background refresh over a held list; and the PRD step's cards.
/// </summary>
public sealed class ProjectsViewTests
{
    private static readonly ProjectDto Held = new(Guid.NewGuid(), "Held", "", "1.0", "", "main", DateTime.UnixEpoch, null, []);

    /// <summary>
    ///     A load the component started shows the spinner, even over a held list.
    ///     Red: drop ownLoad from ShowSpinner; the held list shows instead.
    /// </summary>
    [Fact]
    public void Its_own_load_shows_the_spinner()
    {
        var state = Refreshing(Loaded());

        ProjectsView.ShowSpinner(ownLoad: true, state).Should().BeTrue();
        ProjectsView.Cards(ownLoad: true, state).Should().BeNull();
    }

    /// <summary>
    ///     A background refresh, such as a reconnect's, over a held list shows no spinner and keeps the list.
    ///     Red: return state.Loading from ShowSpinner, as the components first did; the spinner shows.
    ///     Red: drop the Current condition from ShowSpinner; the spinner shows over the held list.
    /// </summary>
    [Fact]
    public void A_background_refresh_over_a_held_list_shows_no_spinner()
    {
        var state = Refreshing(Loaded());

        ProjectsView.ShowSpinner(ownLoad: false, state).Should().BeFalse();
        ProjectsView.Cards(ownLoad: false, state).Should().Equal(Held);
    }

    /// <summary>
    ///     A refresh begun during the component's first load can have that load's answer dropped as stale; until the
    ///     refresh lands there is nothing to show, so the spinner stays rather than "No projects" flashing.
    ///     Red: return ownLoad alone from ShowSpinner; the empty state shows.
    /// </summary>
    [Fact]
    public void A_load_with_nothing_yet_to_show_keeps_the_spinner()
    {
        var state = ProjectsReducers.On(ProjectsState.Initial(null!), new ProjectsLoadStarted(1));
        state = ProjectsReducers.On(state, new ProjectsLoadStarted(2));
        state = ProjectsReducers.On(state, new ProjectsLoaded(1, [Held]));

        ProjectsView.ShowSpinner(ownLoad: false, state).Should().BeTrue("the newest load has not landed");
    }

    /// <summary>
    ///     A load that ended with nothing to show, such as one abandoned for a session being renewed, shows no spinner on
    ///     the page: it shows the empty state, as it always did.
    ///     Red: drop the Loading clause from ShowSpinner; the spinner shows with no load in flight.
    /// </summary>
    [Fact]
    public void A_load_that_ended_with_nothing_shows_no_spinner()
    {
        var state = ProjectsReducers.On(ProjectsState.Initial(null!), new ProjectsLoadStarted(1));
        state = ProjectsReducers.On(state, new ProjectsLoadAbandoned(1));

        ProjectsView.ShowSpinner(ownLoad: false, state).Should().BeFalse();
    }

    /// <summary>
    ///     A failed load shows no spinner, and the step shows it as no projects, as it always did.
    ///     Red: show the spinner whenever Current is null, dropping the Error and Loading clauses; the spinner shows.
    ///     Red: return state.Projects from Cards whatever the error; the held list shows over the failure.
    /// </summary>
    [Fact]
    public void A_failed_load_shows_as_no_projects()
    {
        var state = ProjectsReducers.On(Refreshing(Loaded()), new ProjectsLoadFailed(2, "boom"));

        ProjectsView.ShowSpinner(ownLoad: false, state).Should().BeFalse();
        ProjectsView.Cards(ownLoad: false, state).Should().BeEmpty();
    }

    private static ProjectsState Loaded()
    {
        var state = ProjectsReducers.On(ProjectsState.Initial(null!), new ProjectsLoadStarted(1));
        return ProjectsReducers.On(state, new ProjectsLoaded(1, [Held]));
    }

    private static ProjectsState Refreshing(ProjectsState state) => ProjectsReducers.On(state, new ProjectsLoadStarted(2));
}
