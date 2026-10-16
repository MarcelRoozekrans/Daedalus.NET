namespace Daedalus.Web.State.Tasks;

/// <summary>The board is being fetched.</summary>
public readonly record struct TasksLoadStarted;

/// <summary>The board arrived.</summary>
public readonly record struct TasksLoaded(IReadOnlyList<TaskDto> Items, int Total);

/// <summary>
///     The Tasks page was left. The board is dropped, so a run change or a reconnect no longer re-fetches what no page
///     shows; a start in flight still finishes.
/// </summary>
public readonly record struct TasksClosed;

/// <summary>The board could not be fetched.</summary>
public readonly record struct TasksLoadFailed(string Error);

/// <summary>
///     The board got no answer because the session is being renewed: the load ends and no error is stored, because the
///     layout already says "Signing in again".
/// </summary>
public readonly record struct TasksLoadAbandoned;

/// <summary>The person closed the board's error alert.</summary>
public readonly record struct TasksErrorDismissed;

/// <summary>A task's Manufacture call was sent.</summary>
public readonly record struct TaskStartRequested(Guid Id);

/// <summary>A task's Manufacture call returned, either way.</summary>
public readonly record struct TaskStartFinished(Guid Id);

/// <summary>A task was re-fetched.</summary>
public readonly record struct TaskUpdated(TaskDto Task);
