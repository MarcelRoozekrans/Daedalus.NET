using System.Net;
using Daedalus.Application.DTOs;
using Daedalus.Web.Services;
using Daedalus.Web.State.Projects;
using Daedalus.Web.State.Tasks;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.Flux;
using ZeroAlloc.Flux.Generated;

namespace Daedalus.Tests.Unit.Web;

/// <summary>
///     The projects effects: the one load the Tasks page, the task create dialog and Home's New task share, its
///     reconnect refresh, and what it never stores.
/// </summary>
public sealed class ProjectsEffectsTests : IDisposable
{
    private const string ProjectsPath = "/api/projects?page=1&pageSize=200";

    private readonly RecordingHandler _handler = new();
    private readonly ServiceProvider _provider = new ServiceCollection().AddZeroAllocFlux().BuildServiceProvider();
    private readonly IStore<ProjectsState> _store;
    private readonly ProjectsEffects _effects;

    public ProjectsEffectsTests()
    {
        _store = _provider.GetRequiredService<IStore<ProjectsState>>();
        _effects = new ProjectsEffects(_handler.Client(), _provider.GetRequiredService<IDispatcher>(), _store);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _handler.Dispose();
    }

    /// <summary>
    ///     Loading the projects keeps them and names each one by its id, for the board's Project column, and ends loading.
    ///     Red: key every name by Guid.Empty instead of the project's id; the id lookup misses.
    ///     Red: drop the ProjectsLoaded dispatch; the names stay empty.
    ///     Red: leave Loading set in the ProjectsLoaded reducer.
    /// </summary>
    [Fact]
    public async Task Loading_names_each_project_by_its_id()
    {
        var id = Guid.NewGuid();
        _handler.OnJson(ProjectsPath, RunViews.Json(new PagedResultDto<ProjectDto>([Project(id, "Widgets")], 1, 1, 200)));

        await _effects.LoadAsync(CancellationToken.None);

        _store.Value.Names.Should().ContainKey(id).WhoseValue.Should().Be("Widgets");
        _store.Value.Projects.Should().ContainSingle().Which.Id.Should().Be(id);
        _store.Value.Loading.Should().BeFalse();
    }

    /// <summary>
    ///     A failed load is the projects' error, and ends loading.
    ///     Red: dispatch nothing on a failure; the error stays null.
    ///     Red: leave Loading set in the ProjectsLoadFailed reducer.
    /// </summary>
    [Fact]
    public async Task A_failed_load_is_the_error()
    {
        _handler.OnJson(ProjectsPath, "{}", HttpStatusCode.InternalServerError);

        await _effects.LoadAsync(CancellationToken.None);

        _store.Value.Error.Should().NotBeNull();
        _store.Value.Loading.Should().BeFalse();
    }

    /// <summary>
    ///     A reconnect leaves projects never asked for alone, and re-fetches them once a load was made, even one that
    ///     failed.
    ///     Red: re-fetch whatever the state; the first refresh makes a request.
    ///     Red: gate on Projects being loaded instead of LoadRequested; the failed load is not re-fetched.
    ///     Red: RefreshAsync fetches nothing; the second refresh makes no request.
    /// </summary>
    [Fact]
    public async Task A_refresh_re_fetches_only_projects_that_were_asked_for()
    {
        _handler.OnJson(ProjectsPath, "{}", HttpStatusCode.BadGateway);

        await _effects.RefreshAsync(CancellationToken.None);
        _handler.Requests.Should().BeEmpty("no reader asked for the projects");

        await _effects.LoadAsync(CancellationToken.None);
        _handler.Requests.Clear();
        await _effects.RefreshAsync(CancellationToken.None);
        _handler.Paths.Should().Equal(ProjectsPath);
    }

    /// <summary>
    ///     The projects survive the Tasks page closing: the feature is shared and never cleared on close, so Home's New
    ///     task, opened as the board is disposed, still has them.
    ///     Red: add a ProjectsReducers.On for TasksClosed that drops the projects; they are gone.
    /// </summary>
    [Fact]
    public async Task The_projects_survive_the_tasks_page_closing()
    {
        _handler.OnJson(ProjectsPath, RunViews.Json(new PagedResultDto<ProjectDto>([Project(Guid.NewGuid(), "Widgets")], 1, 1, 200)));
        await _effects.LoadAsync(CancellationToken.None);

        await _provider.GetRequiredService<IDispatcher>().DispatchAsync(new TasksClosed());

        _store.Value.Projects.Should().ContainSingle();
        _store.Value.Names.Should().ContainSingle();
    }

    /// <summary>
    ///     Home opens the create dialog with no board loaded, so loading the projects is not a board load that a
    ///     reconnect would then re-fetch.
    ///     Red: dispatch TasksLoadStarted from ProjectsEffects.LoadAsync; the board's LoadRequested is set.
    /// </summary>
    [Fact]
    public async Task Loading_the_projects_is_not_a_board_load()
    {
        _handler.OnJson(ProjectsPath, RunViews.Json(new PagedResultDto<ProjectDto>([Project(Guid.NewGuid(), "Widgets")], 1, 1, 200)));

        await _effects.LoadAsync(CancellationToken.None);

        _store.Value.Projects.Should().HaveCount(1);
        _provider.GetRequiredService<IStore<TasksState>>().Value.LoadRequested.Should().BeFalse("loading the projects is not a board load");
    }

    /// <summary>
    ///     Two readers open close together: the first one's answer lands after the second's and does not replace it.
    ///     Red: drop the sequence check from the ProjectsLoaded reducer; the older list wins.
    ///     Red: number every load 1 in LoadAsync; the older list wins.
    /// </summary>
    [Fact]
    public async Task An_older_answer_does_not_replace_a_newer_one()
    {
        var older = Guid.NewGuid();
        var newer = Guid.NewGuid();
        var release = new TaskCompletionSource();
        var calls = 0;
        _handler.On(ProjectsPath, async () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                await release.Task;
                return RecordingHandler.Json(HttpStatusCode.OK, RunViews.Json(new PagedResultDto<ProjectDto>([Project(older, "Old")], 1, 1, 200)));
            }

            return RecordingHandler.Json(HttpStatusCode.OK, RunViews.Json(new PagedResultDto<ProjectDto>([Project(newer, "New")], 1, 1, 200)));
        });

        var first = _effects.LoadAsync(CancellationToken.None);
        await _effects.LoadAsync(CancellationToken.None);
        release.SetResult();
        await first;

        _store.Value.Projects.Should().ContainSingle().Which.Id.Should().Be(newer);
        _store.Value.Names.Keys.Should().Equal(newer);
    }

    /// <summary>
    ///     A load whose token cannot be refreshed stores no error, because the browser is already on its way to sign in;
    ///     the projects already loaded stay, and loading ends.
    ///     Red: dispatch ProjectsLoadFailed for SigningInAgain too; the error is the signing-in text.
    ///     Red: dispatch nothing for SigningInAgain; Loading stays set.
    /// </summary>
    [Fact]
    public async Task Signing_in_again_stores_no_error()
    {
        var kept = Guid.NewGuid();
        _handler.OnJson(ProjectsPath, RunViews.Json(new PagedResultDto<ProjectDto>([Project(kept, "Kept")], 1, 1, 200)));
        await _effects.LoadAsync(CancellationToken.None);
        var navigation = new RecordingNavigation();
        _handler.On(ProjectsPath, HttpResponseMessage () => throw new AccessTokenNotAvailableException(navigation, navigation.RefreshFailed(), scopes: null));

        await _effects.LoadAsync(CancellationToken.None);

        _store.Value.Error.Should().BeNull("a session being renewed is not an error to show");
        _store.Value.Loading.Should().BeFalse();
        _store.Value.Projects.Should().ContainSingle().Which.Id.Should().Be(kept);
    }

    /// <summary>
    ///     A load cancelled because its reader closed stores no error: the list is shared and never cleared, so a stored
    ///     "Request was cancelled" would show under the next reader's picker.
    ///     Red: dispatch ProjectsLoadFailed for a cancelled load; the error is "Request was cancelled".
    /// </summary>
    [Fact]
    public async Task A_load_cancelled_by_its_reader_stores_no_error()
    {
        using var closing = new CancellationTokenSource();
        _handler.On(ProjectsPath, async () =>
        {
            await closing.CancelAsync();
            throw new OperationCanceledException(closing.Token);
        });

        await _effects.LoadAsync(closing.Token);

        _store.Value.Error.Should().BeNull();
        _store.Value.Loading.Should().BeFalse();
    }

    /// <summary>
    ///     A load that times out while its reader is still open is the error: only the reader's own cancellation is
    ///     abandoned. HttpClient reports a timeout as a TaskCanceledException, which ApiClient words like a cancellation.
    ///     Red: abandon on the error text "Request was cancelled" instead of on the token; the error stays null.
    /// </summary>
    [Fact]
    public async Task A_timed_out_load_is_the_error()
    {
        _handler.On(ProjectsPath, HttpResponseMessage () => throw new TaskCanceledException("The request timed out."));

        await _effects.LoadAsync(CancellationToken.None);

        _store.Value.Error.Should().Be("Request was cancelled");
        _store.Value.Loading.Should().BeFalse();
    }

    /// <summary>
    ///     A list held from before, with one project, is not settled while the newest load is in flight, so the create
    ///     dialog does not preselect from it; once the fresh answer of two projects lands, nothing is preselected.
    ///     Red: drop the Loading condition from ProjectsState.Settled; the held project is preselected while the fresh
    ///     answer is held back.
    /// </summary>
    [Fact]
    public async Task No_preselect_from_a_held_list_while_the_newest_load_is_in_flight()
    {
        var held = Guid.NewGuid();
        _handler.OnJson(ProjectsPath, RunViews.Json(new PagedResultDto<ProjectDto>([Project(held, "Held")], 1, 1, 200)));
        await _effects.LoadAsync(CancellationToken.None);
        var release = new TaskCompletionSource();
        _handler.On(ProjectsPath, async () =>
        {
            await release.Task;
            return RecordingHandler.Json(HttpStatusCode.OK, RunViews.Json(new PagedResultDto<ProjectDto>(
                [Project(held, "Held"), Project(Guid.NewGuid(), "Fresh")], 2, 1, 200)));
        });

        var fresh = _effects.LoadAsync(CancellationToken.None);
        Preselected().Should().BeNull("the held list is not settled while the newest load is in flight");

        release.SetResult();
        await fresh;
        _store.Value.Settled.Should().HaveCount(2);
        Preselected().Should().BeNull("two projects preselect none");
    }

    private Guid? Preselected() => _store.Value.Settled is { } projects ? TaskForm.Preselect(projects) : null;

    private static ProjectDto Project(Guid id, string name) => new(id, name, "", "1.0", "", "main", DateTime.UnixEpoch, null, []);
}
