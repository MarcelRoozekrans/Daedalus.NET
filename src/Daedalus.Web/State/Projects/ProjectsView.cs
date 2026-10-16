namespace Daedalus.Web.State.Projects;

/// <summary>
///     What a projects list renders, for the Projects page and the PRD project step alike. Pure: the components pass
///     whether a load they started is in flight, and the state.
/// </summary>
public static class ProjectsView
{
    /// <summary>
    ///     Whether the list shows its spinner: while a load the component started is in flight, and while any load is in
    ///     flight with nothing yet to show and no failure. The second keeps the spinner when a reconnect's refresh, begun
    ///     during the component's first load, has its answer dropped as stale. A background refresh over a held list
    ///     shows none.
    /// </summary>
    public static bool ShowSpinner(bool ownLoad, ProjectsState state) =>
        ownLoad || (state.Current is null && state.Error is null && state.Loading);

    /// <summary>
    ///     The projects the PRD project step shows, or <see langword="null"/> for its spinner. A failed load shows as no
    ///     projects, as the step always showed it; a session being renewed, with nothing loaded, keeps the spinner, as
    ///     the layout says so and the browser is on its way to sign in.
    /// </summary>
    public static IReadOnlyList<ProjectDto>? Cards(bool ownLoad, ProjectsState state)
    {
        if (ShowSpinner(ownLoad, state))
        {
            return null;
        }

        return state.Error is not null ? [] : state.Projects;
    }
}
