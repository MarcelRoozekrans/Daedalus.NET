using System.Net;
using Daedalus.Application.DTOs.WorkflowRuns;
using Daedalus.Web.Services;
using Daedalus.Web.State.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.Flux;
using ZeroAlloc.Flux.Generated;

namespace Daedalus.Tests.Unit.Web;

/// <summary>The tasks effects: the board load, the per-task re-fetch on a run change, and the start guard.</summary>
public sealed class TasksEffectsTests : IDisposable
{
    private const string ListPath = "/api/tasks?page=1&pageSize=200";

    private readonly RecordingHandler _handler = new();
    private readonly ServiceProvider _provider = new ServiceCollection().AddZeroAllocFlux().BuildServiceProvider();
    private readonly IStore<TasksState> _store;
    private readonly TasksEffects _effects;

    public TasksEffectsTests()
    {
        _store = _provider.GetRequiredService<IStore<TasksState>>();
        _effects = new TasksEffects(_handler.Client(), _provider.GetRequiredService<IDispatcher>(), _store);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _handler.Dispose();
    }

    /// <summary>
    ///     Loading the board sets its items and the server's total, and ends loading.
    ///     Red: leave Items out of the TasksLoaded reducer. Red: dispatch the item count as the total.
    ///     Red: leave Loading set in the TasksLoaded reducer.
    /// </summary>
    [Fact]
    public async Task Load_sets_the_items_and_the_total()
    {
        var first = Board(Guid.NewGuid(), runId: null, "one");
        var second = Board(Guid.NewGuid(), runId: null, "two");
        _handler.OnJson(ListPath, RunViews.Json(new PagedResultDto<TaskDto>([first, second], 7, 1, 200)));

        await _effects.LoadAsync(CancellationToken.None);

        _store.Value.Items.Select(t => t.Id).Should().Equal(first.Id, second.Id);
        _store.Value.Total.Should().Be(7);
        _store.Value.Loading.Should().BeFalse();
    }

    /// <summary>
    ///     A change for a task's run re-fetches that task and replaces it on the board.
    ///     Red: match the change on the task's Id instead of its WorkflowRunId. Red: drop the TaskUpdated dispatch.
    /// </summary>
    [Fact]
    public async Task A_change_for_a_tasks_run_re_fetches_that_task()
    {
        var runId = Guid.NewGuid();
        var watched = Board(Guid.NewGuid(), runId, "before");
        var bystander = Board(Guid.NewGuid(), Guid.NewGuid(), "bystander");
        _handler.OnJson(ListPath, RunViews.Json(new PagedResultDto<TaskDto>([watched, bystander], 2, 1, 200)))
            .OnJson($"/api/tasks/{watched.Id}", RunViews.Json(watched with { Title = "after" }));
        await _effects.LoadAsync(CancellationToken.None);
        _handler.Requests.Clear();

        await _effects.OnRunChangedAsync(new WorkflowRunChanged(runId, "Succeeded", "done", null), CancellationToken.None);

        _handler.Paths.Should().Equal($"/api/tasks/{watched.Id}");
        _store.Value.Items.Select(t => t.Title).Should().Equal("after", "bystander");
    }

    /// <summary>
    ///     A change for a run no listed task belongs to fetches nothing.
    ///     Red: re-fetch every listed task on any change.
    /// </summary>
    [Fact]
    public async Task A_change_for_no_listed_task_fetches_nothing()
    {
        var listed = Board(Guid.NewGuid(), Guid.NewGuid(), "listed");
        _handler.OnJson(ListPath, RunViews.Json(new PagedResultDto<TaskDto>([listed], 1, 1, 200)));
        await _effects.LoadAsync(CancellationToken.None);
        _handler.Requests.Clear();

        await _effects.OnRunChangedAsync(new WorkflowRunChanged(Guid.NewGuid(), "Running", "build", null), CancellationToken.None);

        _handler.Requests.Should().BeEmpty();
    }

    /// <summary>
    ///     A reconnect re-fetches a loaded board and leaves a board never loaded alone.
    ///     Red: drop the reload from RefreshAsync. Red: reload even when the board is empty.
    /// </summary>
    [Fact]
    public async Task Reconnect_re_fetches_only_a_loaded_board()
    {
        _handler.OnJson(ListPath, RunViews.Json(new PagedResultDto<TaskDto>([Board(Guid.NewGuid(), null, "one")], 1, 1, 200)));

        await _effects.RefreshAsync(CancellationToken.None);
        _handler.Requests.Should().BeEmpty();

        await _effects.LoadAsync(CancellationToken.None);
        _handler.Requests.Clear();
        await _effects.RefreshAsync(CancellationToken.None);
        _handler.Paths.Should().Equal(ListPath);
    }

    /// <summary>
    ///     Manufacture marks the task as starting while the call is in flight, returns the run id, and clears the flag
    ///     on success and on failure.
    ///     Red: never dispatch TaskStartRequested. Red: return a fixed failure when the API succeeds.
    ///     Red: replace the API's refusal with a fixed text. Red: dispatch TaskStartFinished only on success.
    /// </summary>
    [Fact]
    public async Task Manufacture_returns_the_run_and_clears_the_starting_flag()
    {
        var started = Guid.NewGuid();
        var refused = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var startingDuringCall = new List<bool>();
        _handler
            .On($"/api/tasks/{started}/manufacture", () =>
            {
                startingDuringCall.Add(_store.Value.Starting.Contains(started));
                return RecordingHandler.Json(HttpStatusCode.Created, $$"""{"runId":"{{runId}}"}""");
            })
            .On($"/api/tasks/{refused}/manufacture", () =>
                RecordingHandler.Json(HttpStatusCode.Conflict, """{"status":409,"detail":"A run is already live."}"""));

        var success = await _effects.ManufactureAsync(started, CancellationToken.None);
        var failure = await _effects.ManufactureAsync(refused, CancellationToken.None);

        startingDuringCall.Should().Equal(true);
        success.Value.RunId.Should().Be(runId);
        failure.Error.Should().Be("A run is already live.");
        _store.Value.Starting.Should().BeEmpty();
    }

    private static TaskDto Board(Guid id, Guid? runId, string title) =>
        new(id, "T-1", Guid.NewGuid(), title, "", 1, "build", 0, [], [], 1, "", "", 1, 0, null, null, 0,
            DateTime.UnixEpoch, null, null, null, [], runId, null);
}
