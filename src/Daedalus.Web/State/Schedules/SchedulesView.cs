namespace Daedalus.Web.State.Schedules;

/// <summary>What the Schedules page shows in place of its overview.</summary>
public enum SchedulesShow
{
    /// <summary>The grid's spinner and the mobile skeleton cards.</summary>
    Loading,

    /// <summary>The note that no schedule is configured.</summary>
    Empty,

    /// <summary>The grid and the cards.</summary>
    Loaded,

    /// <summary>The failure, with Retry.</summary>
    Error,
}

/// <summary>
///     What the Schedules page renders for its overview. Pure: the page passes the state. Only the page loads the overview,
///     and nothing reloads it in the background, so a read in flight is always the page's own and shows the spinner.
/// </summary>
public static class SchedulesView
{
    /// <summary>
    ///     The failure once the newest read failed; the spinner while it is in flight, and after it ended with the session
    ///     being renewed, as the page always showed it; the note when no schedule is configured; the overview otherwise.
    /// </summary>
    public static SchedulesShow Overview(SchedulesState state) => state switch
    {
        { Error: not null } => SchedulesShow.Error,
        { Overview: null } => SchedulesShow.Loading,
        { Overview.Count: 0 } => SchedulesShow.Empty,
        _ => SchedulesShow.Loaded,
    };
}
