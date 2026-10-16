using System.Net;
using Daedalus.Application.DTOs;
using Daedalus.Web.Services;
using Daedalus.Web.State.Projects;
using Daedalus.Web.State.Tasks;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Flux;
using ZeroAlloc.Flux.Generated;

namespace Daedalus.Tests.Unit.Web;

/// <summary>
///     The projects effects: the one load the Projects page, the Tasks page, the task create dialog, Home's New task and
///     the PRD project step share, its reconnect refresh, the Projects page's selected project and its writes, and what
///     they never store.
/// </summary>
public sealed class ProjectsEffectsTests : IDisposable
{
    private const string ProjectsPath = "/api/projects?page=1&pageSize=200";

    private readonly RecordingHandler _handler = new();
    private readonly ServiceProvider _provider = new ServiceCollection().AddZeroAllocFlux().BuildServiceProvider();
    private readonly IStore<ProjectsState> _store;
    private readonly ProjectsEffects _effects;
    private readonly RecordingLogger<ProjectsEffects> _logger = new();

    public ProjectsEffectsTests()
    {
        _store = _provider.GetRequiredService<IStore<ProjectsState>>();
        _effects = new ProjectsEffects(_handler.Client(), _handler.ProjectClient(), _provider.GetRequiredService<IDispatcher>(), _store, _logger);
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
    ///     Red: log the failure before the SigningInAgain check in LoadAsync; the renewal writes a warning.
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
        _logger.Entries.Should().BeEmpty("a session being renewed is not a failure");
    }

    /// <summary>
    ///     A load cancelled because its reader closed stores no error: the list is shared and never cleared, so a stored
    ///     "Request was cancelled" would show under the next reader's picker.
    ///     Red: dispatch ProjectsLoadFailed for a cancelled load; the error is "Request was cancelled".
    ///     Red: log the failure in the abandoned branch of LoadAsync; the cancellation writes a warning.
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
        _logger.Entries.Should().BeEmpty("a reader closing is not a failure");
    }

    /// <summary>
    ///     A load for a reader that has already gone dispatches nothing and sends nothing: the list, the sequence and the
    ///     loading flag of a reader opened since stay, so that reader's own answer still lands.
    ///     Red: drop the cancelled-token guard in LoadAsync; the sequence moves and the newer reader's answer is dropped.
    /// </summary>
    [Fact]
    public async Task A_load_for_a_reader_already_gone_changes_nothing()
    {
        _handler.OnJson(ProjectsPath, RunViews.Json(new PagedResultDto<ProjectDto>([Project(Guid.NewGuid(), "Widgets")], 1, 1, 200)));
        await _effects.LoadAsync(CancellationToken.None);
        var sequence = _store.Value.Sequence;
        using var gone = new CancellationTokenSource();
        await gone.CancelAsync();

        await _effects.LoadAsync(gone.Token);

        _store.Value.Sequence.Should().Be(sequence);
        _store.Value.Projects.Should().ContainSingle();
        _handler.Requests.Should().ContainSingle("the reader that asked is gone");
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

    /// <summary>
    ///     A refresh that fails leaves the held one-project list in the picker but not settled, so the create dialog does
    ///     not preselect from a list the newest load could not confirm.
    ///     Red: drop the Error condition from ProjectsState.Settled; the held project is preselected after the failure.
    ///     Red: Settled is never the list; the confirmed one-project list is not preselected before the refresh.
    /// </summary>
    [Fact]
    public async Task No_preselect_from_a_held_list_after_a_failed_refresh()
    {
        var held = Guid.NewGuid();
        _handler.OnJson(ProjectsPath, RunViews.Json(new PagedResultDto<ProjectDto>([Project(held, "Held")], 1, 1, 200)));
        await _effects.LoadAsync(CancellationToken.None);
        Preselected().Should().Be(held, "a confirmed one-project list is preselected");
        _handler.OnJson(ProjectsPath, "{}", HttpStatusCode.InternalServerError);

        await _effects.LoadAsync(CancellationToken.None);

        _store.Value.Projects.Should().ContainSingle("the held list stays in the picker");
        Preselected().Should().BeNull("a failed refresh does not confirm the held list");
    }

    // ── The Projects page's selected project ───────────────────────────────────

    /// <summary>
    ///     Selecting a project shows it with its tasks, and clears an earlier click's failure.
    ///     Red: drop the ProjectSelectLoaded dispatch from LoadSelectedAsync; nothing is selected.
    ///     Red: leave Selected unset in the ProjectSelectLoaded reducer.
    ///     Red: set SelectedId to null in the ProjectSelectLoaded reducer; the project asked for is lost.
    ///     Red: keep SelectError in the ProjectSelectRequested reducer; the earlier failure is still the error.
    /// </summary>
    [Fact]
    public async Task Selecting_a_project_shows_it_with_its_tasks()
    {
        var id = Guid.NewGuid();
        _handler.OnJson(WithTasks(id), "{}", HttpStatusCode.InternalServerError);
        await _effects.LoadSelectedAsync(id, CancellationToken.None);
        _store.Value.SelectError.Should().NotBeNull("the earlier click failed");
        _handler.OnJson(WithTasks(id), RunViews.Json(Project(id, "Widgets")));

        await _effects.LoadSelectedAsync(id, CancellationToken.None);

        _store.Value.Selected.Should().NotBeNull().And.Subject.As<ProjectDto>().Id.Should().Be(id);
        _store.Value.SelectedId.Should().Be(id);
        _store.Value.SelectError.Should().BeNull();
    }

    /// <summary>
    ///     A failed select is the select error, and shows no project, as the page showed none after a failure.
    ///     Red: dispatch nothing on a failure; the error stays null.
    ///     Red: keep Selected in the ProjectSelectFailed reducer; the project selected before still shows.
    /// </summary>
    [Fact]
    public async Task A_failed_select_is_the_error_and_shows_no_project()
    {
        var id = Guid.NewGuid();
        _handler.OnJson(WithTasks(id), RunViews.Json(Project(id, "Widgets")));
        await _effects.LoadSelectedAsync(id, CancellationToken.None);
        _handler.OnJson(WithTasks(id), "{}", HttpStatusCode.InternalServerError);

        await _effects.LoadSelectedAsync(id, CancellationToken.None);

        _store.Value.SelectError.Should().NotBeNull();
        _store.Value.Selected.Should().BeNull();
    }

    /// <summary>
    ///     A select whose token cannot be refreshed stores no error, and shows no project, as the page showed none.
    ///     Red: dispatch ProjectSelectFailed for SigningInAgain too; the error is the signing-in text.
    ///     Red: dispatch nothing for SigningInAgain; the project selected before still shows.
    /// </summary>
    [Fact]
    public async Task Signing_in_again_on_a_select_stores_no_error()
    {
        var id = Guid.NewGuid();
        _handler.OnJson(WithTasks(id), RunViews.Json(Project(id, "Widgets")));
        await _effects.LoadSelectedAsync(id, CancellationToken.None);
        _handler.On(WithTasks(id), SigningIn());

        await _effects.LoadSelectedAsync(id, CancellationToken.None);

        _store.Value.SelectError.Should().BeNull("a session being renewed is not an error to show");
        _store.Value.Selected.Should().BeNull();
    }

    /// <summary>
    ///     A select cancelled because the page closed stores no error, and shows no project.
    ///     Red: drop the cancellation check from LoadSelectedAsync; the error is "Request was cancelled".
    /// </summary>
    [Fact]
    public async Task A_select_cancelled_by_its_page_stores_no_error()
    {
        var id = Guid.NewGuid();
        using var closing = new CancellationTokenSource();
        _handler.On(WithTasks(id), async () =>
        {
            await closing.CancelAsync();
            throw new OperationCanceledException(closing.Token);
        });

        await _effects.LoadSelectedAsync(id, closing.Token);

        _store.Value.SelectError.Should().BeNull("the page that asked is gone");
        _store.Value.Selected.Should().BeNull();
    }

    /// <summary>
    ///     Two clicks close together: the first project's answer lands after the second's and does not replace it.
    ///     Red: drop the id check from the ProjectSelectLoaded reducer; the first project shows.
    /// </summary>
    [Fact]
    public async Task A_late_answer_for_an_earlier_click_is_dropped()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var release = new TaskCompletionSource();
        _handler.On(WithTasks(first), async () =>
        {
            await release.Task;
            return RecordingHandler.Json(HttpStatusCode.OK, RunViews.Json(Project(first, "First")));
        });
        _handler.OnJson(WithTasks(second), RunViews.Json(Project(second, "Second")));

        var late = _effects.LoadSelectedAsync(first, CancellationToken.None);
        await _effects.LoadSelectedAsync(second, CancellationToken.None);
        release.SetResult();
        await late;

        _store.Value.Selected.Should().NotBeNull().And.Subject.As<ProjectDto>().Id.Should().Be(second);
    }

    // ── The Projects page's writes ────────────────────────────────────────────

    /// <summary>
    ///     A select for a page that has already gone dispatches nothing: the project the reopened page asked for stays
    ///     the one asked for, so its answer still lands.
    ///     Red: drop the cancelled-token guard in LoadSelectedAsync; SelectedId moves to the gone page's project.
    /// </summary>
    [Fact]
    public async Task A_select_for_a_page_already_gone_changes_nothing()
    {
        var shown = Guid.NewGuid();
        _handler.OnJson(WithTasks(shown), RunViews.Json(Project(shown, "Shown")));
        await _effects.LoadSelectedAsync(shown, CancellationToken.None);
        using var gone = new CancellationTokenSource();
        await gone.CancelAsync();

        await _effects.LoadSelectedAsync(Guid.NewGuid(), gone.Token);

        _store.Value.SelectedId.Should().Be(shown);
        _store.Value.Selected.Should().NotBeNull();
    }

    /// <summary>
    ///     A created project returns a saved outcome, and the create posts what was asked.
    ///     Red: SettleAsync returns null on a save; the returned outcome is missing.
    ///     Red: post the project with an empty name; the request body has no "Atlas".
    /// </summary>
    [Fact]
    public async Task A_created_project_is_saved()
    {
        _handler.OnJson("/api/projects", RunViews.Json(Project(Guid.NewGuid(), "Atlas")));

        var outcome = await _effects.CreateAsync(new CreateProjectDto("Atlas", "d", "1.0"), CancellationToken.None);

        outcome.Should().Be(new ProjectSave(ProjectWrite.Created, null));
        _handler.Requests.Should().ContainSingle(r => r.Method == HttpMethod.Post).Which.Body.Should().Contain("Atlas");
    }

    /// <summary>
    ///     A refused create returns the refusal, worded as the dialog always worded it, and is not a save.
    ///     Red: SettleAsync returns null on a refusal; the returned outcome is missing.
    ///     Red: return a fixed "Failed to create" instead of the client's error; the server's reason is lost.
    ///     Red: make ProjectSave.Succeeded always true; the refusal reads as saved.
    /// </summary>
    [Fact]
    public async Task A_refused_create_is_the_refusal()
    {
        _handler.On("/api/projects", () => Text(HttpStatusCode.BadRequest, "Name taken"));

        var outcome = await _effects.CreateAsync(new CreateProjectDto("Atlas", "d", "1.0"), CancellationToken.None);

        outcome.Should().Be(new ProjectSave(ProjectWrite.Created, "Failed to create project: Name taken"));
        outcome!.Value.Succeeded.Should().BeFalse();
    }

    /// <summary>
    ///     A create whose token cannot be refreshed returns no outcome: the dialog says nothing and stays open, as it
    ///     did.
    ///     Red: drop the SigningInAgain check in SettleAsync; the outcome is the signing-in text.
    /// </summary>
    [Fact]
    public async Task Signing_in_again_on_a_create_is_no_outcome()
    {
        _handler.On("/api/projects", SigningIn());

        var outcome = await _effects.CreateAsync(new CreateProjectDto("Atlas", "d", "1.0"), CancellationToken.None);

        outcome.Should().BeNull("a session being renewed is not an outcome to show");
    }

    /// <summary>
    ///     An updated project returns a saved outcome, and the update puts what was asked to that project.
    ///     Red: return a save of Created for every write; the outcome's write is wrong.
    ///     Red: put to Guid.Empty instead of the id; the request goes to another path.
    /// </summary>
    [Fact]
    public async Task An_updated_project_is_saved()
    {
        var id = Guid.NewGuid();
        _handler.OnJson(ProjectPath(id), RunViews.Json(Project(id, "Atlas")));

        var outcome = await _effects.UpdateAsync(id, new UpdateProjectDto("Atlas", "d", "1.0"), CancellationToken.None);

        outcome.Should().Be(new ProjectSave(ProjectWrite.Updated, null));
        _handler.Requests.Should().ContainSingle().Which.Should().Match<RecordedRequest>(r =>
            r.Method == HttpMethod.Put && r.PathAndQuery == ProjectPath(id) && r.Body!.Contains("Atlas"));
    }

    /// <summary>
    ///     A refused update returns the refusal, worded as the dialog always worded it.
    ///     Red: return a save on a failure in SettleAsync; the outcome is a success.
    ///     Red: return the refusal as ProjectWrite.Created; the write is wrong.
    /// </summary>
    [Fact]
    public async Task A_refused_update_is_the_refusal()
    {
        var id = Guid.NewGuid();
        _handler.On(ProjectPath(id), () => Text(HttpStatusCode.Conflict, "Stale"));

        var outcome = await _effects.UpdateAsync(id, new UpdateProjectDto("Atlas", "d", "1.0"), CancellationToken.None);

        outcome.Should().Be(new ProjectSave(ProjectWrite.Updated, "Failed to update project: Stale"));
    }

    /// <summary>
    ///     An update whose token cannot be refreshed returns no outcome, and is not logged.
    ///     Red: log the refusal before the SigningInAgain check in SettleAsync; the renewal writes a warning.
    /// </summary>
    [Fact]
    public async Task Signing_in_again_on_an_update_is_no_outcome()
    {
        var id = Guid.NewGuid();
        _handler.On(ProjectPath(id), SigningIn());

        var outcome = await _effects.UpdateAsync(id, new UpdateProjectDto("Atlas", "d", "1.0"), CancellationToken.None);

        outcome.Should().BeNull();
        _logger.Entries.Should().BeEmpty("a session being renewed is not a failure");
    }

    /// <summary>
    ///     Deleting the project the page shows returns a saved outcome and stops showing it; deleting another keeps it.
    ///     Red: drop the ProjectSaved dispatch in SettleAsync; the deleted project still shows.
    ///     Red: leave Selected in the ProjectSaved reducer for a delete; the deleted project still shows.
    ///     Red: clear Selected for any delete, whatever its id; the other delete hides the shown project.
    ///     Red: send the delete as a GET in ProjectApiClient.DeleteProjectAsync; the last request is not a DELETE.
    /// </summary>
    [Fact]
    public async Task Deleting_the_shown_project_stops_showing_it()
    {
        var shown = Guid.NewGuid();
        var other = Guid.NewGuid();
        _handler.OnJson(WithTasks(shown), RunViews.Json(Project(shown, "Shown")));
        _handler.On(ProjectPath(shown), () => new HttpResponseMessage(HttpStatusCode.NoContent));
        _handler.On(ProjectPath(other), () => new HttpResponseMessage(HttpStatusCode.NoContent));
        await _effects.LoadSelectedAsync(shown, CancellationToken.None);

        await _effects.DeleteAsync(other, CancellationToken.None);
        _store.Value.Selected.Should().NotBeNull("another project was deleted");

        var outcome = await _effects.DeleteAsync(shown, CancellationToken.None);
        outcome.Should().Be(new ProjectSave(ProjectWrite.Deleted, null));
        _store.Value.Selected.Should().BeNull();
        _handler.Requests[^1].Method.Should().Be(HttpMethod.Delete);
    }

    /// <summary>
    ///     A refused delete returns the refusal, and the project still shows.
    ///     Red: dispatch ProjectSaved on a failure in SettleAsync; the project no longer shows.
    ///     Red: return the refusal as ProjectWrite.Created; the write is wrong.
    /// </summary>
    [Fact]
    public async Task A_refused_delete_is_the_refusal()
    {
        var id = Guid.NewGuid();
        _handler.OnJson(WithTasks(id), RunViews.Json(Project(id, "Shown")));
        _handler.On(ProjectPath(id), () => Text(HttpStatusCode.Conflict, "In use"));
        await _effects.LoadSelectedAsync(id, CancellationToken.None);

        var outcome = await _effects.DeleteAsync(id, CancellationToken.None);

        outcome.Should().Be(new ProjectSave(ProjectWrite.Deleted, "Failed to delete project: In use"));
        _store.Value.Selected.Should().NotBeNull();
    }

    /// <summary>
    ///     A delete whose token cannot be refreshed, or whose page closed meanwhile, returns no outcome.
    ///     Red: drop the SigningInAgain check in SettleAsync; the outcome is the signing-in text.
    ///     Red: drop the cancellation check in SettleAsync; the outcome is the cancellation.
    /// </summary>
    [Fact]
    public async Task Signing_in_again_or_closing_on_a_delete_is_no_outcome()
    {
        var id = Guid.NewGuid();
        _handler.On(ProjectPath(id), SigningIn());
        (await _effects.DeleteAsync(id, CancellationToken.None)).Should().BeNull("a session being renewed is not an outcome to show");

        using var closing = new CancellationTokenSource();
        _handler.On(ProjectPath(id), async () =>
        {
            await closing.CancelAsync();
            throw new OperationCanceledException(closing.Token);
        });
        (await _effects.DeleteAsync(id, closing.Token)).Should().BeNull("a write whose page closed has no one to show it to");
    }

    // ── Logging ─────────────────────────────────────────────────────────────

    /// <summary>
    ///     A failed load writes exactly one warning, with the error text.
    ///     Red: drop the LogLoadFailed call; no warning is written.
    ///     Red: call LogLoadFailed twice; two warnings are written.
    ///     Red: log the fixed text "load failed" instead of the error; the error text is missing.
    /// </summary>
    [Fact]
    public async Task A_failed_load_writes_one_warning()
    {
        _handler.On(ProjectsPath, HttpResponseMessage () => throw new TaskCanceledException("The request timed out."));

        await _effects.LoadAsync(CancellationToken.None);

        _logger.Entries.Should().ContainSingle().Which.Should().Match<(LogLevel Level, string Message)>(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("Request was cancelled"));
    }

    /// <summary>
    ///     A failed select and a refused write each write one warning with the error text; a session being renewed
    ///     writes none.
    ///     Red: drop the LogSelectFailed call; the select writes no warning.
    ///     Red: drop the LogWriteRefused call; the refusal writes no warning.
    ///     Red: log before the SigningInAgain check in LoadSelectedAsync; the renewal writes a warning.
    /// </summary>
    [Fact]
    public async Task A_failed_select_and_a_refused_write_each_write_one_warning()
    {
        var id = Guid.NewGuid();
        _handler.On(WithTasks(id), SigningIn());
        await _effects.LoadSelectedAsync(id, CancellationToken.None);
        _logger.Entries.Should().BeEmpty("a session being renewed is not a failure");

        _handler.OnJson(WithTasks(id), "{}", HttpStatusCode.InternalServerError);
        await _effects.LoadSelectedAsync(id, CancellationToken.None);
        _logger.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);

        _handler.On("/api/projects", () => Text(HttpStatusCode.BadRequest, "Name taken"));
        await _effects.CreateAsync(new CreateProjectDto("Atlas", "d", "1.0"), CancellationToken.None);
        _logger.Entries.Should().HaveCount(2);
        _logger.Entries[^1].Message.Should().Contain("Failed to create project: Name taken");
    }

    private static string WithTasks(Guid id) => $"/api/projects/{id}/with-tasks";

    private static string ProjectPath(Guid id) => $"/api/projects/{id}";

    private static Func<HttpResponseMessage> SigningIn()
    {
        var navigation = new RecordingNavigation();
        return () => throw new AccessTokenNotAvailableException(navigation, navigation.RefreshFailed(), scopes: null);
    }

    private static HttpResponseMessage Text(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body) };

    private Guid? Preselected() => _store.Value.Settled is { } projects ? TaskForm.Preselect(projects) : null;

    private static ProjectDto Project(Guid id, string name) => new(id, name, "", "1.0", "", "main", DateTime.UnixEpoch, null, []);
}
