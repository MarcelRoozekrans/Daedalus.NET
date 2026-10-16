using System.Net;
using Daedalus.Application.DTOs;
using Daedalus.Application.DTOs.WorkflowRuns;
using Daedalus.Web.Services;
using Daedalus.Web.State.Tasks;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Flux;
using ZeroAlloc.Flux.Generated;

namespace Daedalus.Tests.Unit.Web;

/// <summary>
///     The tasks effects: the board load, the per-task re-fetch on a run change, and the start guard. The projects
///     cases moved to <see cref="ProjectsEffectsTests"/> and <see cref="ProjectsReducersTests"/>.
/// </summary>
public sealed class TasksEffectsTests : IDisposable
{
    private const string ListPath = "/api/tasks?page=1&pageSize=200";

    private readonly RecordingHandler _handler = new();
    private readonly ServiceProvider _provider = new ServiceCollection().AddZeroAllocFlux().BuildServiceProvider();
    private readonly IStore<TasksState> _store;
    private readonly TasksEffects _effects;
    private readonly RecordingLogger<TasksEffects> _logger = new();

    public TasksEffectsTests()
    {
        _store = _provider.GetRequiredService<IStore<TasksState>>();
        _effects = new TasksEffects(_handler.Client(), _provider.GetRequiredService<IDispatcher>(), _store, _logger);
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
    ///     A reconnect leaves a board never asked for alone, and re-fetches one that was, even when its load failed. The
    ///     projects are no longer the board's: ProjectsEffects re-fetches them on its own.
    ///     Red: reload whatever the state. Red: gate on Items.Count instead of LoadRequested.
    ///     Red: drop the reload from RefreshAsync.
    ///     Red: re-fetch the projects from TasksEffects.RefreshAsync, as before; the paths hold the projects too.
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
    ///     Dismissing the error clears it and leaves the board alone, so the same failure after a retry shows again.
    ///     Red: the reducer keeps Error. Red: the reducer clears Items too.
    /// </summary>
    [Fact]
    public void Dismissing_the_error_clears_it_and_keeps_the_board()
    {
        var task = Board(Guid.NewGuid(), null, "kept");
        var state = TasksState.Initial(null!);
        state = TasksReducers.On(state, new TasksLoadStarted());
        state = TasksReducers.On(state, new TasksLoaded([task], 1));
        state = TasksReducers.On(state, new TasksLoadStarted());
        state = TasksReducers.On(state, new TasksLoadFailed("boom"));
        state.Error.Should().Be("boom");

        state = TasksReducers.On(state, new TasksErrorDismissed());

        state.Error.Should().BeNull();
        state.Items.Should().ContainSingle();
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

    /// <summary>
    ///     A board read whose token cannot be refreshed stores no error, because the layout already says "Signing in
    ///     again"; the rows shown stay, loading ends, and the board does not claim to be empty.
    ///     Red: dispatch TasksLoadFailed for SigningInAgain too; the error is the signing-in text.
    ///     Red: dispatch nothing for SigningInAgain; Loading stays set.
    ///     Red: log the failure before the SigningInAgain check in LoadAsync; the renewal writes a warning.
    ///     Red: leave Abandoned unset in the TasksLoadAbandoned reducer; the board says it is empty.
    /// </summary>
    [Fact]
    public async Task Signing_in_again_stores_no_error_and_claims_no_empty_board()
    {
        _handler.On(ListPath, SigningIn());

        await _effects.LoadAsync(CancellationToken.None);

        _store.Value.Error.Should().BeNull("a session being renewed is not an error to show");
        _store.Value.Loading.Should().BeFalse();
        TasksView.ShowEmpty(_store.Value).Should().BeFalse("a board that was not read is not known to be empty");
        _logger.Entries.Should().BeEmpty("a session being renewed is not a failure");

        var kept = Board(Guid.NewGuid(), null, "kept");
        _handler.OnJson(ListPath, RunViews.Json(new PagedResultDto<TaskDto>([kept], 1, 1, 200)));
        await _effects.LoadAsync(CancellationToken.None);
        _handler.On(ListPath, SigningIn());

        await _effects.LoadAsync(CancellationToken.None);

        _store.Value.Items.Should().ContainSingle().Which.Id.Should().Be(kept.Id);
    }

    /// <summary>
    ///     The board says it is empty only once a read came back empty: not while loading, not after a failure, not
    ///     after an abandoned read, and again after a read that follows the abandoned one.
    ///     Red: drop Loading from ShowEmpty. Red: drop the Error check. Red: drop the Abandoned check.
    ///     Red: drop the LoadRequested check; the initial board says it is empty.
    ///     Red: keep Abandoned in the TasksLoadStarted reducer; the next read never says empty.
    /// </summary>
    [Fact]
    public void Only_a_board_read_empty_says_it_is_empty()
    {
        var initial = TasksState.Initial(null!);
        var loading = TasksReducers.On(initial, new TasksLoadStarted());
        var failed = TasksReducers.On(loading, new TasksLoadFailed("boom"));
        var abandoned = TasksReducers.On(loading, new TasksLoadAbandoned());
        var readAgain = TasksReducers.On(TasksReducers.On(abandoned, new TasksLoadStarted()), new TasksLoaded([], 0));

        TasksView.ShowEmpty(initial).Should().BeFalse();
        TasksView.ShowEmpty(loading).Should().BeFalse();
        TasksView.ShowEmpty(failed).Should().BeFalse();
        TasksView.ShowEmpty(abandoned).Should().BeFalse();
        TasksView.ShowEmpty(readAgain).Should().BeTrue();
    }

    /// <summary>
    ///     An abandoned read that lands after the page closed, with no load asked for since, changes nothing.
    ///     Red: drop the LoadRequested guard in the TasksLoadAbandoned reducer.
    /// </summary>
    [Fact]
    public void An_abandoned_read_after_the_page_closed_is_ignored()
    {
        var closed = TasksReducers.On(TasksReducers.On(TasksState.Initial(null!), new TasksLoadStarted()), new TasksClosed());

        TasksReducers.On(closed, new TasksLoadAbandoned()).Should().Be(closed);
    }

    /// <summary>
    ///     A caller whose token is already cancelled dispatches nothing: no board is asked for and no start is marked.
    ///     Red: drop the IsCancellationRequested guard at the top of LoadAsync; LoadRequested is set.
    ///     Red: drop the guard before TaskStartRequested in ManufactureAsync; the start is marked during the call.
    /// </summary>
    [Fact]
    public async Task A_cancelled_caller_dispatches_nothing()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var marks = new List<int>();
        _store.StateChanged += state => marks.Add(state.Starting.Count);

        await _effects.LoadAsync(cancelled.Token);
        var started = await _effects.ManufactureAsync(Guid.NewGuid(), cancelled.Token);

        _store.Value.LoadRequested.Should().BeFalse();
        started.IsFailure.Should().BeTrue();
        marks.Should().OnlyContain(count => count == 0, "no start was marked");
    }

    /// <summary>
    ///     A board, a task or a start whose caller went away while it was in flight stores no error and logs nothing.
    ///     Red: drop the cancelled branch in LoadAsync; the board stores "Request was cancelled".
    ///     Red: drop the cancellation return in OnRunChangedAsync; the task's failure is logged.
    ///     Red: drop the cancellation check before LogManufactureRefused; the start logs it.
    /// </summary>
    [Fact]
    public async Task A_failure_after_the_caller_went_away_stores_nothing()
    {
        var runId = Guid.NewGuid();
        var watched = Board(Guid.NewGuid(), runId, "watched");
        _handler.OnJson(ListPath, RunViews.Json(new PagedResultDto<TaskDto>([watched], 1, 1, 200)));
        await _effects.LoadAsync(CancellationToken.None);
        using var boardGone = new CancellationTokenSource();
        using var taskGone = new CancellationTokenSource();
        using var startGone = new CancellationTokenSource();
        _handler.On(ListPath, CancelThenThrow(boardGone))
            .On($"/api/tasks/{watched.Id}", CancelThenThrow(taskGone))
            .On($"/api/tasks/{watched.Id}/manufacture", CancelThenThrow(startGone));

        await _effects.LoadAsync(boardGone.Token);
        await _effects.OnRunChangedAsync(new WorkflowRunChanged(runId, "Running", "build", null), taskGone.Token);
        await _effects.ManufactureAsync(watched.Id, startGone.Token);

        _store.Value.Error.Should().BeNull();
        _logger.Entries.Should().BeEmpty();
    }

    /// <summary>
    ///     A board read, a task re-fetch and a refused start are each logged as a warning with the error text only; a
    ///     session being renewed logs nothing.
    ///     Red: drop LogLoadFailed. Red: drop LogTaskFailed. Red: drop LogManufactureRefused.
    ///     Red: drop the SigningInAgain check before LogTaskFailed. Red: drop the one before LogManufactureRefused.
    /// </summary>
    [Fact]
    public async Task Failures_are_logged_with_the_error_text()
    {
        var runId = Guid.NewGuid();
        var watched = Board(Guid.NewGuid(), runId, "watched");
        _handler.OnJson(ListPath, RunViews.Json(new PagedResultDto<TaskDto>([watched], 1, 1, 200)));
        await _effects.LoadAsync(CancellationToken.None);
        _handler.OnJson(ListPath, """{"status":502,"detail":"Board down."}""", HttpStatusCode.BadGateway)
            .OnJson($"/api/tasks/{watched.Id}", """{"status":500,"detail":"Task down."}""", HttpStatusCode.InternalServerError)
            .OnJson($"/api/tasks/{watched.Id}/manufacture", """{"status":409,"detail":"A run is already live."}""", HttpStatusCode.Conflict);

        await _effects.OnRunChangedAsync(new WorkflowRunChanged(runId, "Running", "build", null), CancellationToken.None);
        await _effects.ManufactureAsync(watched.Id, CancellationToken.None);
        await _effects.LoadAsync(CancellationToken.None);

        _logger.Entries.Should().HaveCount(3).And.OnlyContain(e => e.Level == LogLevel.Warning);
        _logger.Entries[0].Message.Should().StartWith("A task could not be re-fetched after its run changed: ");
        _logger.Entries[1].Message.Should().Be("A manufacture start was refused: A run is already live.");
        _logger.Entries[2].Message.Should().Be($"The tasks could not be loaded: {_store.Value.Error}");

        _logger.Entries.Clear();
        _handler.OnJson(ListPath, RunViews.Json(new PagedResultDto<TaskDto>([watched], 1, 1, 200)));
        await _effects.LoadAsync(CancellationToken.None);
        _handler.On($"/api/tasks/{watched.Id}", SigningIn()).On($"/api/tasks/{watched.Id}/manufacture", SigningIn());

        await _effects.OnRunChangedAsync(new WorkflowRunChanged(runId, "Running", "build", null), CancellationToken.None);
        await _effects.ManufactureAsync(watched.Id, CancellationToken.None);

        _logger.Entries.Should().BeEmpty("a session being renewed is not a failure");
    }

    private static Func<HttpResponseMessage> SigningIn()
    {
        var navigation = new RecordingNavigation();
        return () => throw new AccessTokenNotAvailableException(navigation, navigation.RefreshFailed(), scopes: null);
    }

    private static Func<Task<HttpResponseMessage>> CancelThenThrow(CancellationTokenSource gone) => async () =>
    {
        await gone.CancelAsync();
        throw new OperationCanceledException(gone.Token);
    };

    private static TaskDto Board(Guid id, Guid? runId, string title) =>
        new(id, "T-1", Guid.NewGuid(), title, "", 1, "build", 0, [], [], 1, "", "", 1, 0, null, null, 0,
            DateTime.UnixEpoch, null, null, null, [], runId, null, runId is null ? null : "Running", null);
}
