using ZeroAlloc.Flux;

namespace Daedalus.Web.State.Tasks;

/// <summary>The board list, as the Tasks page shows it.</summary>
/// <param name="Items">The tasks on the board.</param>
/// <param name="Total">How many tasks the server holds.</param>
/// <param name="Loading">Whether the board is being fetched.</param>
/// <param name="Error">Why the last fetch failed, or <see langword="null"/>.</param>
/// <param name="Starting">The ids of the tasks whose Manufacture call is in flight.</param>
/// <param name="LoadRequested">
///     Whether a board load was ever asked for, whatever came of it. Set by <see cref="TasksLoadStarted"/> rather
///     than on success, so a reconnect also re-fetches a board that came back empty or failed to load.
/// </param>
/// <param name="Projects">
///     The projects, the one fetch both the board's Project column and the task dialogs' picker read; null until they
///     have loaded.
/// </param>
/// <param name="ProjectNames">Each project's name by its id, for the board's Project column.</param>
/// <param name="ProjectsError">Why the last projects fetch failed, or <see langword="null"/>.</param>
[Feature(InitialState = nameof(Initial))]
public readonly partial record struct TasksState(
    IReadOnlyList<TaskDto> Items, int Total, bool Loading, string? Error, IReadOnlySet<Guid> Starting, bool LoadRequested,
    IReadOnlyList<ProjectDto>? Projects, IReadOnlyDictionary<Guid, string> ProjectNames, string? ProjectsError)
{
    /// <summary>The state before the board has loaded.</summary>
    public static TasksState Initial(IServiceProvider services) =>
        new([], 0, false, null, new HashSet<Guid>(), false, null, new Dictionary<Guid, string>(), null);
}
