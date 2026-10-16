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

    /// <summary>
    ///     Marks the project asked for and clears the last select error. The project shown stays until the answer lands,
    ///     as the page always kept it.
    /// </summary>
    [Reducer]
    public static ProjectsState On(ProjectsState s, ProjectSelectRequested a) =>
        s with { SelectedId = a.Id, SelectError = null };

    /// <summary>Kept only when it is the project asked for last; see <see cref="ProjectsState.SelectedId"/>.</summary>
    [Reducer]
    public static ProjectsState On(ProjectsState s, ProjectSelectLoaded a) =>
        a.Project.Id == s.SelectedId ? s with { Selected = a.Project } : s;

    /// <summary>A failure shows no project, as the page showed none after one.</summary>
    [Reducer]
    public static ProjectsState On(ProjectsState s, ProjectSelectFailed a) =>
        a.Id == s.SelectedId ? s with { Selected = null, SelectError = a.Error } : s;

    /// <summary>A session being renewed shows no project and stores no error.</summary>
    [Reducer]
    public static ProjectsState On(ProjectsState s, ProjectSelectAbandoned a) =>
        a.Id == s.SelectedId ? s with { Selected = null } : s;

    /// <summary>Clears the last outcome, so one that ends with no outcome is never read as the one before.</summary>
    [Reducer]
    public static ProjectsState On(ProjectsState s, ProjectSaveStarted a) => s with { LastSave = null };

    /// <summary>A deleted project that the page shows is shown no longer.</summary>
    [Reducer]
    public static ProjectsState On(ProjectsState s, ProjectSaved a) =>
        a.Write == ProjectWrite.Deleted && a.Id == s.SelectedId
            ? s with { LastSave = new ProjectSave(a.Write, null), Selected = null, SelectedId = null }
            : s with { LastSave = new ProjectSave(a.Write, null) };

    [Reducer]
    public static ProjectsState On(ProjectsState s, ProjectSaveRefused a) =>
        s with { LastSave = new ProjectSave(a.Write, a.Reason) };

    /// <summary>Clears the Projects page's own fields and leaves the shared list; see <see cref="ProjectsState"/>.</summary>
    [Reducer]
    public static ProjectsState On(ProjectsState s, ProjectsPageClosed a) =>
        s with { Selected = null, SelectedId = null, SelectError = null, LastSave = null };
}
