using ZeroAlloc.Flux;

namespace Daedalus.Web.State.Tasks;

/// <summary>The board list, as the Tasks page shows it.</summary>
/// <param name="Items">The tasks on the board.</param>
/// <param name="Total">How many tasks the server holds.</param>
/// <param name="Loading">Whether the board is being fetched.</param>
/// <param name="Error">Why the last fetch failed, or <see langword="null"/>.</param>
/// <param name="Starting">The ids of the tasks whose Manufacture call is in flight.</param>
[Feature(InitialState = nameof(Initial))]
public readonly partial record struct TasksState(
    IReadOnlyList<TaskDto> Items, int Total, bool Loading, string? Error, IReadOnlySet<Guid> Starting)
{
    /// <summary>The state before the board has loaded.</summary>
    public static TasksState Initial(IServiceProvider services) => new([], 0, false, null, new HashSet<Guid>());
}
