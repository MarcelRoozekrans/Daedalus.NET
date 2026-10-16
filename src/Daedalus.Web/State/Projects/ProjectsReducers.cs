using ZeroAlloc.Flux;

namespace Daedalus.Web.State.Projects;

/// <summary>
///     The projects feature's reducers. Pure: no I/O, no clock. An answer is kept only when no newer load has started
///     since its own; see <see cref="ProjectsState"/>.
/// </summary>
public static class ProjectsReducers
{
    /// <summary>
    ///     Marks the newest load. The last error stays until a load succeeds, so the board's projects alert does not
    ///     flicker away on Retry. A start seen out of order never moves the sequence back.
    /// </summary>
    [Reducer]
    public static ProjectsState On(ProjectsState s, ProjectsLoadStarted a) =>
        s with { Sequence = Math.Max(s.Sequence, a.Sequence), Loading = true, LoadRequested = true };

    [Reducer]
    public static ProjectsState On(ProjectsState s, ProjectsLoaded a) =>
        a.Sequence < s.Sequence
            ? s
            : s with
            {
                Projects = a.Projects,
                Names = a.Projects.ToDictionary(p => p.Id, p => p.ProjectName),
                Error = null,
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
