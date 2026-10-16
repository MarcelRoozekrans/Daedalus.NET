using System.Net;
using Daedalus.Application.DTOs.WorkflowRuns;
using Daedalus.Web.State.Runs;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Flux;
using ZeroAlloc.Flux.Generated;

namespace Daedalus.Tests.Unit.Web;

/// <summary>The runs effects: what they fetch on each trigger, and that failures become actions, not exceptions.</summary>
public sealed class RunsEffectsTests : IDisposable
{
    private const string CountPath = "/api/workflow-runs?take=100&status=Awaiting";

    private readonly RecordingHandler _handler = new();
    private readonly ServiceProvider _provider = new ServiceCollection().AddZeroAllocFlux().BuildServiceProvider();
    private readonly IStore<RunsState> _store;
    private readonly RunsEffects _effects;
    private readonly RecordingLogger<RunsEffects> _logger = new();

    public RunsEffectsTests()
    {
        _store = _provider.GetRequiredService<IStore<RunsState>>();
        _effects = new RunsEffects(_handler.Client(), _provider.GetRequiredService<IDispatcher>(), _store, _logger);
        _handler.OnJson(CountPath, RunViews.Json(new WorkflowRunList([], null)));
    }

    public void Dispose()
    {
        _provider.Dispose();
        _handler.Dispose();
    }

    /// <summary>
    ///     A change for the selected run re-fetches it; a change for another run does not.
    ///     Red: re-fetch on every change. Red: never re-fetch the selected run.
    ///     Red: keep the first loaded run in the RunLoaded reducer.
    /// </summary>
    [Fact]
    public async Task A_change_re_fetches_only_the_selected_run()
    {
        var selected = Guid.NewGuid();
        var other = Guid.NewGuid();
        _handler.OnJson($"/api/workflow-runs/{selected}", RunViews.Json(RunViews.Sample(selected)));
        await _effects.LoadRunAsync(selected, CancellationToken.None);
        _handler.Requests.Clear();

        await _effects.OnRunChangedAsync(new WorkflowRunChanged(other, "Running", "build", null), CancellationToken.None);

        _handler.Paths.Should().NotContain(p => p.StartsWith("/api/workflow-runs/", StringComparison.Ordinal));

        _handler.OnJson($"/api/workflow-runs/{selected}", RunViews.Json(RunViews.Sample(selected, "Succeeded", null)));
        await _effects.OnRunChangedAsync(new WorkflowRunChanged(selected, "Succeeded", "done", null), CancellationToken.None);

        _handler.Paths.Should().Contain($"/api/workflow-runs/{selected}");
        _store.Value.Selected!.Status.Should().Be("Succeeded");
    }

    /// <summary>
    ///     A change for a run on the shown page, or any change while the first page is shown, re-fetches the list; a
    ///     change for a run not on a later page does not.
    ///     Red: re-fetch the list only for listed runs. Red: always re-fetch the list.
    ///     Red: re-fetch the list only while the first page is shown.
    /// </summary>
    [Fact]
    public async Task A_change_re_fetches_the_list_when_it_could_show_it()
    {
        var listed = Guid.NewGuid();
        var page = RunViews.Json(new WorkflowRunList([RunViews.Item(listed)], "next"));
        var first = RunListQuery.First;
        var later = RunListQuery.First with { After = "c1" };
        _handler.OnJson(first.ToPath(), page).OnJson(later.ToPath(), page);

        await _effects.LoadListAsync(first, CancellationToken.None);
        _handler.Requests.Clear();
        await _effects.OnRunChangedAsync(new WorkflowRunChanged(Guid.NewGuid(), "Running", "build", null), CancellationToken.None);
        _handler.Paths.Should().Contain(first.ToPath(), "a new run appears on the first page");

        await _effects.LoadListAsync(later, CancellationToken.None);
        _handler.Requests.Clear();
        await _effects.OnRunChangedAsync(new WorkflowRunChanged(Guid.NewGuid(), "Running", "build", null), CancellationToken.None);
        _handler.Paths.Should().NotContain(later.ToPath(), "the run is not on this later page");

        await _effects.OnRunChangedAsync(new WorkflowRunChanged(listed, "Failed", "build", null), CancellationToken.None);
        _handler.Paths.Should().Contain(later.ToPath(), "the run is on this page");
    }

    /// <summary>
    ///     Reconnect re-fetches the selected run, the list and the awaiting-approval count.
    ///     Red: drop any one of the three from RefreshAllAsync.
    /// </summary>
    [Fact]
    public async Task Reconnect_re_fetches_everything_on_screen()
    {
        var id = Guid.NewGuid();
        var later = RunListQuery.First with { After = "c1" };
        _handler.OnJson($"/api/workflow-runs/{id}", RunViews.Json(RunViews.Sample(id)))
            .OnJson(later.ToPath(), RunViews.Json(new WorkflowRunList([], null)));
        await _effects.LoadListAsync(later, CancellationToken.None);
        await _effects.LoadRunAsync(id, CancellationToken.None);
        _handler.Requests.Clear();

        await _effects.RefreshAllAsync(CancellationToken.None);

        _handler.Paths.Should().BeEquivalentTo([$"/api/workflow-runs/{id}", later.ToPath(), CountPath]);
    }

    /// <summary>
    ///     A failed approve records the server's reason on the draft and re-fetches the run; it never throws.
    ///     Red: let the failure escape. Red: skip the re-fetch after a failure.
    ///     Red: return Result.Success after a refusal. Red: drop the GateSubmitFailed dispatch.
    ///     Red: dispatch GateSubmitSucceeded instead of GateSubmitFailed.
    ///     Red: dispatch GateSubmitSucceeded before GateSubmitFailed.
    /// </summary>
    [Fact]
    public async Task A_refused_approve_keeps_the_draft_and_says_why()
    {
        var id = Guid.NewGuid();
        _handler.OnJson($"/api/workflow-runs/{id}", RunViews.Json(RunViews.Sample(id)))
            .OnJson($"/api/workflow-runs/{id}/resume", """{"status":409,"detail":"The run is no longer at the gate."}""",
                HttpStatusCode.Conflict);
        var dispatcher = _provider.GetRequiredService<IDispatcher>();
        await _effects.LoadRunAsync(id, CancellationToken.None);
        await dispatcher.DispatchAsync(new GateFindingToggled("mechanism-1", Dropped: true));
        _handler.Requests.Clear();

        var result = await _effects.ApproveAsync(CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        _store.Value.Gate.Submitting.Should().BeFalse();
        _store.Value.Gate.Error.Should().Be("The run is no longer at the gate.");
        _store.Value.Gate.Dropped.Should().Equal("mechanism-1");
        _handler.Paths.Should().Equal($"/api/workflow-runs/{id}/resume", $"/api/workflow-runs/{id}");
    }

    /// <summary>
    ///     An accepted approval re-fetches the run, so a page with no live connection still sees it leave the gate,
    ///     and the draft resets once it has.
    ///     Red: re-fetch the run only after a refused approve.
    /// </summary>
    [Fact]
    public async Task An_accepted_approve_re_fetches_the_run()
    {
        var id = Guid.NewGuid();
        _handler.OnJson($"/api/workflow-runs/{id}", RunViews.Json(RunViews.Sample(id)))
            .On($"/api/workflow-runs/{id}/resume", () => new HttpResponseMessage(HttpStatusCode.Accepted));
        await _effects.LoadRunAsync(id, CancellationToken.None);
        await _provider.GetRequiredService<IDispatcher>().DispatchAsync(new GateFindingToggled("mechanism-1", Dropped: true));
        _handler.OnJson($"/api/workflow-runs/{id}", RunViews.Json(RunViews.Sample(id, "Running", awaitingSignal: null)));
        _handler.Requests.Clear();

        var result = await _effects.ApproveAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _handler.Paths.Should().Equal($"/api/workflow-runs/{id}/resume", $"/api/workflow-runs/{id}");
        _store.Value.Gate.Should().Be(GateDraft.Empty);
    }

    /// <summary>
    ///     A call made with a token already cancelled, as a page that closed leaves it, re-fetches nothing: cancel and
    ///     retry do not re-open the closed run, and approve neither marks the draft busy nor records a load failure.
    ///     Red: drop the IsCancellationRequested guard at the top of LoadRunAsync; cancel re-opens the closed run.
    ///     Red: drop the guard before GateSubmitStarted in ApproveAsync; the draft stays busy.
    ///     Red: drop the cancellation check before SettleAsync's log; cancel and retry log "Request was cancelled".
    /// </summary>
    [Fact]
    public async Task A_cancelled_call_re_fetches_nothing()
    {
        var id = Guid.NewGuid();
        _handler.OnJson($"/api/workflow-runs/{id}", RunViews.Json(RunViews.Sample(id)));
        var dispatcher = _provider.GetRequiredService<IDispatcher>();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await _effects.LoadRunAsync(id, CancellationToken.None);
        (await _effects.ApproveAsync(cancelled.Token)).IsSuccess.Should().BeFalse();
        _store.Value.SelectedError.Should().BeNull("approve's cancelled call must not re-fetch the run");
        _store.Value.Gate.Submitting.Should().BeFalse("a cancelled approve dispatches nothing");

        await dispatcher.DispatchAsync(new RunClosed());
        (await _effects.CancelAsync(id, null, cancelled.Token)).IsSuccess.Should().BeFalse();
        _store.Value.SelectedId.Should().BeNull("cancel's cancelled call must not re-open the closed run");
        (await _effects.RetryAsync(id, cancelled.Token)).IsSuccess.Should().BeFalse();
        _store.Value.SelectedId.Should().BeNull("retry's cancelled call must not re-open the closed run");
        _logger.Entries.Should().BeEmpty("a caller that went away is not a refusal");
    }

    /// <summary>
    ///     A 403 for the run marks it forbidden rather than missing or failed.
    ///     Red: dispatch RunNotFound for a forbidden lookup.
    /// </summary>
    [Fact]
    public async Task A_forbidden_run_is_marked_forbidden()
    {
        var id = Guid.NewGuid();
        _handler.On($"/api/workflow-runs/{id}", () => new HttpResponseMessage(HttpStatusCode.Forbidden));

        await _effects.LoadRunAsync(id, CancellationToken.None);

        _store.Value.SelectedForbidden.Should().BeTrue();
        _store.Value.SelectedNotFound.Should().BeFalse();
    }

    /// <summary>
    ///     A list page whose first load failed is re-fetched on a change and on a reconnect, so it recovers.
    ///     Red: gate the change's list re-fetch on List is not null. Red: gate RefreshAllAsync on List is not null.
    /// </summary>
    [Fact]
    public async Task A_list_whose_load_failed_is_re_fetched()
    {
        var first = RunListQuery.First;
        _handler.OnJson(first.ToPath(), "<html>down</html>", HttpStatusCode.BadGateway);
        await _effects.LoadListAsync(first, CancellationToken.None);
        _handler.Requests.Clear();

        await _effects.OnRunChangedAsync(new WorkflowRunChanged(Guid.NewGuid(), "Running", "build", null), CancellationToken.None);
        _handler.Paths.Should().Contain(first.ToPath(), "a change re-fetches the list page that failed");

        _handler.Requests.Clear();
        await _effects.RefreshAllAsync(CancellationToken.None);
        _handler.Paths.Should().Contain(first.ToPath(), "a reconnect re-fetches the list page that failed");
    }

    /// <summary>
    ///     Once the run page is left, a change for that run fetches no run, and its gate draft is gone.
    ///     Red: keep SelectedId on RunClosed. Red: keep the Gate on RunClosed.
    /// </summary>
    [Fact]
    public async Task After_the_run_is_closed_a_change_fetches_no_run()
    {
        var id = Guid.NewGuid();
        _handler.OnJson($"/api/workflow-runs/{id}", RunViews.Json(RunViews.Sample(id)));
        var dispatcher = _provider.GetRequiredService<IDispatcher>();
        await _effects.LoadRunAsync(id, CancellationToken.None);
        await dispatcher.DispatchAsync(new GateFindingToggled("mechanism-1", Dropped: true));
        await dispatcher.DispatchAsync(new RunClosed());
        _handler.Requests.Clear();

        await _effects.OnRunChangedAsync(new WorkflowRunChanged(id, "Succeeded", "done", null), CancellationToken.None);

        _handler.Paths.Should().NotContain($"/api/workflow-runs/{id}");
        _store.Value.Gate.Dropped.Should().BeEmpty();
    }

    /// <summary>
    ///     Once the list page is left, a change fetches no list, even for a run the list showed on the first page.
    ///     Red: keep ListRequested on RunListClosed.
    /// </summary>
    [Fact]
    public async Task After_the_list_is_closed_a_change_fetches_no_list()
    {
        var listed = Guid.NewGuid();
        _handler.OnJson(RunListQuery.First.ToPath(), RunViews.Json(new WorkflowRunList([RunViews.Item(listed)], null)));
        await _effects.LoadListAsync(RunListQuery.First, CancellationToken.None);
        await _provider.GetRequiredService<IDispatcher>().DispatchAsync(new RunListClosed());
        _handler.Requests.Clear();

        await _effects.OnRunChangedAsync(new WorkflowRunChanged(listed, "Failed", "build", null), CancellationToken.None);

        _handler.Paths.Should().Equal(CountPath);
    }

    /// <summary>
    ///     A list closed while the open run is being fetched is not fetched again, on a change or on a reconnect: the
    ///     list is judged on the state after the run's fetch.
    ///     Red: judge the change's list re-fetch on the state read before the run's fetch.
    ///     Red: judge RefreshAllAsync's list re-fetch on the state read before the run's fetch.
    /// </summary>
    [Fact]
    public async Task A_list_closed_while_the_run_is_fetched_is_not_fetched_again()
    {
        var id = Guid.NewGuid();
        var dispatcher = _provider.GetRequiredService<IDispatcher>();
        _handler.OnJson(RunListQuery.First.ToPath(), RunViews.Json(new WorkflowRunList([RunViews.Item(id)], null)))
            .OnJson($"/api/workflow-runs/{id}", RunViews.Json(RunViews.Sample(id)));
        await _effects.LoadListAsync(RunListQuery.First, CancellationToken.None);
        await _effects.LoadRunAsync(id, CancellationToken.None);
        _handler.On($"/api/workflow-runs/{id}", async () =>
        {
            await dispatcher.DispatchAsync(new RunListClosed());
            return RecordingHandler.Json(HttpStatusCode.OK, RunViews.Json(RunViews.Sample(id)));
        });

        _handler.Requests.Clear();
        await _effects.OnRunChangedAsync(new WorkflowRunChanged(id, "Running", "build", null), CancellationToken.None);
        _handler.Paths.Should().Equal([$"/api/workflow-runs/{id}", CountPath], "the list was closed during the run's fetch");

        await _effects.LoadListAsync(RunListQuery.First, CancellationToken.None);
        _handler.Requests.Clear();
        await _effects.RefreshAllAsync(CancellationToken.None);
        _handler.Paths.Should().Equal([$"/api/workflow-runs/{id}", CountPath], "the list was closed during the run's fetch");
    }

    /// <summary>
    ///     The awaiting-approval count counts only human_approval rows, and says there are more when Next is set.
    ///     Red: count every Awaiting row. Red: ignore Next.
    /// </summary>
    [Fact]
    public async Task The_count_counts_only_approval_gates()
    {
        _handler.OnJson(CountPath, RunViews.Json(new WorkflowRunList(
            [
                RunViews.Item(Guid.NewGuid(), "Awaiting", "human_approval"),
                RunViews.Item(Guid.NewGuid(), "Awaiting", "other_signal"),
                RunViews.Item(Guid.NewGuid(), "Awaiting", "human_approval"),
            ],
            "more")));

        await _effects.LoadAwaitingApprovalAsync(CancellationToken.None);

        _store.Value.AwaitingApproval.Should().Be(2);
        _store.Value.AwaitingApprovalMore.Should().BeTrue();
    }

    /// <summary>
    ///     A list read whose token cannot be refreshed stores no error, because the layout already says "Signing in
    ///     again"; the page shown stays, and loading ends.
    ///     Red: dispatch RunListFailed for SigningInAgain too; the error is the signing-in text.
    ///     Red: dispatch nothing for SigningInAgain; ListLoading stays set.
    ///     Red: log the failure before the SigningInAgain check in LoadListAsync; the renewal writes a warning.
    /// </summary>
    [Fact]
    public async Task Signing_in_again_stores_no_list_error()
    {
        var listed = Guid.NewGuid();
        _handler.OnJson(RunListQuery.First.ToPath(), RunViews.Json(new WorkflowRunList([RunViews.Item(listed)], null)));
        await _effects.LoadListAsync(RunListQuery.First, CancellationToken.None);
        _handler.On(RunListQuery.First.ToPath(), SigningIn());

        await _effects.LoadListAsync(RunListQuery.First, CancellationToken.None);

        _store.Value.ListError.Should().BeNull("a session being renewed is not an error to show");
        _store.Value.ListLoading.Should().BeFalse();
        _store.Value.List!.Runs.Should().ContainSingle().Which.Id.Should().Be(listed);
        _logger.Entries.Should().BeEmpty("a session being renewed is not a failure");
    }

    /// <summary>
    ///     A run read whose token cannot be refreshed stores no error, and its first load ends.
    ///     Red: dispatch RunLoadFailed for SigningInAgain too; the error is the signing-in text.
    ///     Red: dispatch nothing for SigningInAgain; SelectedLoading stays set.
    ///     Red: log the failure before the SigningInAgain check in LoadRunAsync; the renewal writes a warning.
    /// </summary>
    [Fact]
    public async Task Signing_in_again_stores_no_run_error()
    {
        var id = Guid.NewGuid();
        _handler.On($"/api/workflow-runs/{id}", SigningIn());

        await _effects.LoadRunAsync(id, CancellationToken.None);

        _store.Value.SelectedId.Should().Be(id);
        _store.Value.SelectedError.Should().BeNull("a session being renewed is not an error to show");
        _store.Value.SelectedLoading.Should().BeFalse();
        _logger.Entries.Should().BeEmpty("a session being renewed is not a failure");
    }

    /// <summary>
    ///     An approval whose token cannot be refreshed frees the draft and stores no error; the choices stay.
    ///     Red: dispatch GateSubmitFailed for SigningInAgain too; the error is the signing-in text.
    ///     Red: dispatch nothing for SigningInAgain; the draft stays busy.
    ///     Red: log the refusal before the SigningInAgain check in ApproveAsync; the renewal writes a warning.
    /// </summary>
    [Fact]
    public async Task Signing_in_again_on_approve_stores_no_gate_error()
    {
        var id = Guid.NewGuid();
        _handler.OnJson($"/api/workflow-runs/{id}", RunViews.Json(RunViews.Sample(id)))
            .OnJson($"/api/workflow-runs/{id}/resume", """{"status":409,"detail":"Refused before."}""", HttpStatusCode.Conflict);
        await _effects.LoadRunAsync(id, CancellationToken.None);
        await _provider.GetRequiredService<IDispatcher>().DispatchAsync(new GateFindingToggled("mechanism-1", Dropped: true));
        await _effects.ApproveAsync(CancellationToken.None);
        _logger.Entries.Clear();
        _handler.On($"/api/workflow-runs/{id}/resume", SigningIn());

        var result = await _effects.ApproveAsync(CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        _store.Value.Gate.Error.Should().BeNull("a session being renewed is not an error to show");
        _store.Value.Gate.Submitting.Should().BeFalse();
        _store.Value.Gate.Dropped.Should().Equal("mechanism-1");
        _logger.Entries.Should().BeEmpty("a session being renewed is not a failure");
    }

    /// <summary>
    ///     Every failure the effects store or drop is logged as a warning with the error text, and only that.
    ///     Red: drop LogListFailed. Red: drop LogRunFailed. Red: drop LogCountFailed.
    ///     Red: drop the approve refusal's LogWriteRefused. Red: drop the refusal log in SettleAsync; cancel and retry log
    ///     nothing.
    /// </summary>
    [Fact]
    public async Task Failures_are_logged_with_the_error_text()
    {
        var id = Guid.NewGuid();
        _handler.OnJson(RunListQuery.First.ToPath(), """{"status":502,"detail":"List down."}""", HttpStatusCode.BadGateway)
            .OnJson($"/api/workflow-runs/{id}", """{"status":500,"detail":"Run down."}""", HttpStatusCode.InternalServerError)
            .OnJson(CountPath, """{"status":502,"detail":"Count down."}""", HttpStatusCode.BadGateway);

        await _effects.LoadListAsync(RunListQuery.First, CancellationToken.None);
        await _effects.LoadRunAsync(id, CancellationToken.None);
        await _effects.LoadAwaitingApprovalAsync(CancellationToken.None);

        _logger.Entries.Should().HaveCount(3).And.OnlyContain(e => e.Level == LogLevel.Warning);
        _logger.Entries[0].Message.Should().StartWith("The runs could not be loaded: ").And.Be($"The runs could not be loaded: {_store.Value.ListError}");
        _logger.Entries[1].Message.Should().Be($"A run could not be loaded: {_store.Value.SelectedError}");
        _logger.Entries[2].Message.Should().StartWith("The awaiting-approval count could not be loaded: ");

        _handler.OnJson($"/api/workflow-runs/{id}", RunViews.Json(RunViews.Sample(id)))
            .OnJson($"/api/workflow-runs/{id}/resume", """{"status":409,"detail":"Not at the gate."}""", HttpStatusCode.Conflict)
            .OnJson($"/api/workflow-runs/{id}/cancel", """{"status":409,"detail":"Already done."}""", HttpStatusCode.Conflict)
            .OnJson($"/api/workflow-runs/{id}/retry", """{"status":409,"detail":"Nothing failed."}""", HttpStatusCode.Conflict);
        await _effects.LoadRunAsync(id, CancellationToken.None);
        _logger.Entries.Clear();

        await _effects.ApproveAsync(CancellationToken.None);
        await _effects.CancelAsync(id, "a reason that is not logged", CancellationToken.None);
        await _effects.RetryAsync(id, CancellationToken.None);

        _logger.Entries.Select(e => e.Message).Should().Equal(
            "A run write (Approve) was refused: Not at the gate.",
            "A run write (Cancel) was refused: Already done.",
            "A run write (Retry) was refused: Nothing failed.");
    }

    /// <summary>
    ///     A session being renewed is not a failure for the count, cancel or retry either: nothing is logged.
    ///     Red: drop the SigningInAgain check before LogCountFailed. Red: drop the SigningInAgain check in SettleAsync.
    /// </summary>
    [Fact]
    public async Task Signing_in_again_logs_nothing()
    {
        var id = Guid.NewGuid();
        _handler.On(CountPath, SigningIn())
            .On($"/api/workflow-runs/{id}/cancel", SigningIn())
            .OnJson($"/api/workflow-runs/{id}", RunViews.Json(RunViews.Sample(id)));

        await _effects.LoadAwaitingApprovalAsync(CancellationToken.None);
        await _effects.CancelAsync(id, null, CancellationToken.None);

        _logger.Entries.Should().BeEmpty();
    }

    /// <summary>
    ///     A list, a run or an approval whose caller went away while it was in flight stores no error and logs nothing:
    ///     the page's close ended the load, and a page opened since owns what shows now.
    ///     Red: drop the cancelled branch in LoadListAsync; the list stores "Request was cancelled".
    ///     Red: drop the cancelled return in LoadRunAsync's failure; the run stores it.
    ///     Red: drop the cancelled return after the approve call; the draft stores it.
    ///     Red: drop the cancellation check before LogCountFailed; the count logs it.
    /// </summary>
    [Fact]
    public async Task A_failure_after_the_caller_went_away_stores_nothing()
    {
        var id = Guid.NewGuid();
        using var listGone = new CancellationTokenSource();
        using var runGone = new CancellationTokenSource();
        using var approveGone = new CancellationTokenSource();
        using var countGone = new CancellationTokenSource();
        _handler.On(RunListQuery.First.ToPath(), CancelThenThrow(listGone))
            .On($"/api/workflow-runs/{id}", CancelThenThrow(runGone))
            .On(CountPath, CancelThenThrow(countGone));

        await _effects.LoadListAsync(RunListQuery.First, listGone.Token);
        await _effects.LoadRunAsync(id, runGone.Token);
        await _effects.LoadAwaitingApprovalAsync(countGone.Token);

        _store.Value.ListError.Should().BeNull();
        _store.Value.SelectedError.Should().BeNull();

        _handler.OnJson($"/api/workflow-runs/{id}", RunViews.Json(RunViews.Sample(id)))
            .On($"/api/workflow-runs/{id}/resume", CancelThenThrow(approveGone));
        await _effects.LoadRunAsync(id, CancellationToken.None);

        await _effects.ApproveAsync(approveGone.Token);

        _store.Value.Gate.Error.Should().BeNull();
        _logger.Entries.Should().BeEmpty();
    }

    /// <summary>
    ///     A caller whose token is already cancelled dispatches nothing: no list is asked for and no run is opened.
    ///     Red: drop the IsCancellationRequested guard at the top of LoadListAsync; the query and ListRequested change.
    ///     Red: drop the guard at the top of LoadRunAsync; the run is opened.
    /// </summary>
    [Fact]
    public async Task A_cancelled_caller_asks_for_no_list_and_opens_no_run()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var filtered = new RunListQuery("Failed", null, null);

        await _effects.LoadListAsync(filtered, cancelled.Token);
        await _effects.LoadRunAsync(Guid.NewGuid(), cancelled.Token);

        _store.Value.ListRequested.Should().BeFalse();
        _store.Value.Query.Should().Be(RunListQuery.First);
        _store.Value.SelectedId.Should().BeNull();
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
}
