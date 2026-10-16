namespace Daedalus.Web.State.Tasks;

/// <summary>What the Tasks page shows for a board state, decided without a renderer.</summary>
public static class TasksView
{
    /// <summary>
    ///     Whether the board says it is empty. Only a board that was read and is empty says so. One that could not be
    ///     read is not empty: the alert says why. One still loading, not yet asked for, or whose read was abandoned while
    ///     the session is renewed, has nothing to claim either.
    /// </summary>
    public static bool ShowEmpty(TasksState state) =>
        state is { LoadRequested: true, Loading: false, Abandoned: false } && string.IsNullOrEmpty(state.Error);
}
