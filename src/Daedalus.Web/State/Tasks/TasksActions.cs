namespace Daedalus.Web.State.Tasks;

/// <summary>The board is being fetched.</summary>
public readonly record struct TasksLoadStarted;

/// <summary>The board arrived.</summary>
public readonly record struct TasksLoaded(IReadOnlyList<TaskDto> Items, int Total);

/// <summary>The board could not be fetched.</summary>
public readonly record struct TasksLoadFailed(string Error);

/// <summary>A task's Manufacture call was sent.</summary>
public readonly record struct TaskStartRequested(Guid Id);

/// <summary>A task's Manufacture call returned, either way.</summary>
public readonly record struct TaskStartFinished(Guid Id);

/// <summary>A task was re-fetched.</summary>
public readonly record struct TaskUpdated(TaskDto Task);
