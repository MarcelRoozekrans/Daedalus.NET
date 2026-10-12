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
    ///     A reconnect leaves a board never asked for alone, and re-fetches one that was, even when its load failed.
    ///     Red: reload whatever the state. Red: gate on Items.Count instead of LoadRequested.
    ///     Red: drop the reload from RefreshAsync.
    /// </summary>
    [Fact]
    public async Task Reconnect_re_fetches_only_a_board_that_was_asked_for()
    {
        _handler.OnJson(ListPath, "<html>down</html>", HttpStatusCode.BadGateway);

        await _effects.RefreshAsync(CancellationToken.None);
        _handler.Requests.Should().BeEmpty();

        // The board's load fails with a 502, so it holds no items.
        await _effects.LoadAsync(CancellationToken.None);
        _handler.Requests.Clear();
        await _effects.RefreshAsync(CancellationToken.None);
        _handler.Paths.Should().Equal(ListPath);
    }

    /// <summary>
    ///     Once the Tasks page is left, a change for a listed task's run fetches no task.
    ///     Red: keep Items on TasksClosed.
    /// </summary>
    [Fact]
    public async Task After_the_board_is_closed_a_change_fetches_no_task()
    {
        var runId = Guid.NewGuid();
        var watched = Board(Guid.NewGuid(), runId, "watched");
        _handler.OnJson(ListPath, RunViews.Json(new PagedResultDto<TaskDto>([watched], 1, 1, 200)))
            .OnJson($"/api/tasks/{watched.Id}", RunViews.Json(watched));
        await _effects.LoadAsync(CancellationToken.None);
        await _provider.GetRequiredService<IDispatcher>().DispatchAsync(new TasksClosed());
        _handler.Requests.Clear();

        await _effects.OnRunChangedAsync(new WorkflowRunChanged(runId, "Succeeded", "done", null), CancellationToken.None);

        _handler.Requests.Should().BeEmpty();
    }

    /// <summary>
    ///     Once the Tasks page is left, a reconnect loads no board.
    ///     Red: keep LoadRequested on TasksClosed.
    /// </summary>
    [Fact]
    public async Task After_the_board_is_closed_a_reconnect_loads_no_board()
    {
        _handler.OnJson(ListPath, RunViews.Json(new PagedResultDto<TaskDto>([Board(Guid.NewGuid(), null, "one")], 1, 1, 200)));
        await _effects.LoadAsync(CancellationToken.None);
        await _provider.GetRequiredService<IDispatcher>().DispatchAsync(new TasksClosed());
        _handler.Requests.Clear();

        await _effects.RefreshAsync(CancellationToken.None);

        _handler.Requests.Should().BeEmpty();
    }

    /// <summary>
    ///     A board still in flight when the page closed does not come back, and a start in flight survives the close.
    ///     Red: drop the LoadRequested guard in the TasksLoaded reducer. Red: clear Starting on TasksClosed.
    /// </summary>
    [Fact]
    public void A_board_that_lands_after_the_page_closed_is_ignored()
    {
        var starting = Guid.NewGuid();
        var state = TasksState.Initial(null!);
        state = TasksReducers.On(state, new TasksLoadStarted());
        state = TasksReducers.On(state, new TaskStartRequested(starting));
        state = TasksReducers.On(state, new TasksClosed());

        state = TasksReducers.On(state, new TasksLoaded([Board(Guid.NewGuid(), null, "late")], 1));

        state.Items.Should().BeEmpty();
        state.Starting.Should().Equal(starting);
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
