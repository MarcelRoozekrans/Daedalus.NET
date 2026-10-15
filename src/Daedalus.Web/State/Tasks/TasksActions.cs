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

/// <summary>The person closed the board's error alert.</summary>
public readonly record struct TasksErrorDismissed;

/// <summary>A task's Manufacture call was sent.</summary>
public readonly record struct TaskStartRequested(Guid Id);

/// <summary>A task's Manufacture call returned, either way.</summary>
public readonly record struct TaskStartFinished(Guid Id);

/// <summary>A task was re-fetched.</summary>
public readonly record struct TaskUpdated(TaskDto Task);

/// <summary>The projects are being fetched, for the board or for a create dialog opened from Home.</summary>
public readonly record struct TaskProjectsLoadStarted;

/// <summary>The projects arrived, for the board's Project column and the task dialogs' picker.</summary>
public readonly record struct TaskProjectsLoaded(IReadOnlyList<ProjectDto> Projects);

/// <summary>The projects could not be fetched.</summary>
public readonly record struct TaskProjectsLoadFailed(string Error);
