using ZeroAlloc.Flux;

namespace Daedalus.Web.State.Projects;

/// <summary>
///     The projects feature's reducers. Pure: no I/O, no clock. An answer is kept only when no newer load has started
///     since its own; see <see cref="ProjectsState"/>.
/// </summary>
public static class ProjectsReducers
{
    /// <summary>
    ///     Marks the newest load and clears the last error: the state is shared, so one reader's failure must not show
    ///     under another reader's picker while that reader re-fetches. A start seen out of order never moves the
    ///     sequence back.
    /// </summary>
    [Reducer]
    public static ProjectsState On(ProjectsState s, ProjectsLoadStarted a) =>
        s with { Sequence = Math.Max(s.Sequence, a.Sequence), Loading = true, LoadRequested = true, Error = null };

    /// <summary>Needs no error clear: the start of the same load cleared it, and an older failure is dropped.</summary>
    [Reducer]
    public static ProjectsState On(ProjectsState s, ProjectsLoaded a) =>
        a.Sequence < s.Sequence
            ? s
            : s with
            {
                Projects = a.Projects,
                Names = a.Projects.ToDictionary(p => p.Id, p => p.ProjectName),
                Loading = false,
            };

    /// <summary>A failure keeps the projects already loaded, so a failed refresh does not empty the picker.</summary>
    [Reducer]
    public static ProjectsState On(ProjectsState s, ProjectsLoadFailed a) =>
        a.Sequence < s.Sequence ? s : s with { Error = a.Error, Loading = false };

    [Reducer]
    public static ProjectsState On(ProjectsState s, ProjectsLoadAbandoned a) =>
        a.Sequence < s.Sequence ? s : s with { Loading = false };
}
