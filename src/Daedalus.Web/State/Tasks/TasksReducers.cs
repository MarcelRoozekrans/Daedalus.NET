using ZeroAlloc.Flux;

namespace Daedalus.Web.State.Tasks;

/// <summary>The tasks feature's reducers. Pure: no I/O, no clock.</summary>
public static class TasksReducers
{
    [Reducer]
    public static TasksState On(TasksState s, TasksLoadStarted _) => s with { Loading = true, Error = null };

    [Reducer]
    public static TasksState On(TasksState s, TasksLoaded a) => s with { Items = a.Items, Total = a.Total, Loading = false };

    [Reducer]
    public static TasksState On(TasksState s, TasksLoadFailed a) => s with { Error = a.Error, Loading = false };

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
}
