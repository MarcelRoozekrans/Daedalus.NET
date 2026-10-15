using ZeroAlloc.Flux;

namespace Daedalus.Web.State.Tasks;

/// <summary>The tasks feature's reducers. Pure: no I/O, no clock.</summary>
public static class TasksReducers
{
    [Reducer]
    public static TasksState On(TasksState s, TasksLoadStarted _) => s with { Loading = true, Error = null, LoadRequested = true };

    /// <summary>A board that lands after the page closed, with no load asked for since, is ignored.</summary>
    [Reducer]
    public static TasksState On(TasksState s, TasksLoaded a) =>
        !s.LoadRequested ? s : s with { Items = a.Items, Total = a.Total, Loading = false };

    [Reducer]
    public static TasksState On(TasksState s, TasksLoadFailed a) =>
        !s.LoadRequested ? s : s with { Error = a.Error, Loading = false };

    [Reducer]
    public static TasksState On(TasksState s, TasksErrorDismissed _) => s with { Error = null };

    /// <summary>Drops the board but keeps <see cref="TasksState.Starting"/>, so a start in flight still finishes.</summary>
    [Reducer]
    public static TasksState On(TasksState s, TasksClosed _) =>
        s with
        {
            Items = [],
            Total = 0,
            Loading = false,
            Error = null,
            LoadRequested = false,
            Projects = null,
            ProjectNames = new Dictionary<Guid, string>(),
            ProjectsError = null,
        };

    [Reducer]
    public static TasksState On(TasksState s, TaskStartRequested a) => s with { Starting = new HashSet<Guid>(s.Starting) { a.Id } };

    [Reducer]
    public static TasksState On(TasksState s, TaskStartFinished a)
    {
        var starting = new HashSet<Guid>(s.Starting);
        starting.Remove(a.Id);
        return s with { Starting = starting };
    }

    [Reducer]
    public static TasksState On(TasksState s, TaskUpdated a)
    {
        if (!s.Items.Any(t => t.Id == a.Task.Id))
        {
            return s;
        }

        return s with { Items = [.. s.Items.Select(t => t.Id == a.Task.Id ? a.Task : t)] };
    }

    /// <summary>Projects that land after the page closed, with no load asked for since, are ignored.</summary>
    [Reducer]
    public static TasksState On(TasksState s, TaskProjectsLoaded a) =>
        !s.LoadRequested
            ? s
            : s with { Projects = a.Projects, ProjectNames = a.Projects.ToDictionary(p => p.Id, p => p.ProjectName), ProjectsError = null };

    /// <summary>A failure keeps the projects already loaded, so a failed refresh does not empty the picker.</summary>
    [Reducer]
    public static TasksState On(TasksState s, TaskProjectsLoadFailed a) => !s.LoadRequested ? s : s with { ProjectsError = a.Error };
}
