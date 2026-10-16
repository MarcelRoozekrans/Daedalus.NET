using System.Net;
using Daedalus.Application.DTOs.WorkflowRuns;
using Daedalus.Web.Services;
using Daedalus.Web.State.Home;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using ZeroAlloc.Flux;
using ZeroAlloc.Flux.Generated;

namespace Daedalus.Tests.Unit.Web;

/// <summary>
///     The Home effects: the four cards' reads, the role gate, what they never store, and the live re-fetch on a run
///     change or a reconnect, per R6.
/// </summary>
public sealed class HomeEffectsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);

    private static readonly string AwaitingPath = NeedsMe.AwaitingQuery.ToPath();
    private static readonly string RunningPath = NeedsMe.RunningQuery.ToPath();
    private static readonly string FailedPath = NeedsMe.FailedQuery(Now).ToPath();
    private static readonly string PullRequestsPath = NeedsMe.PullRequestQuery.ToPath();

    private static readonly WorkflowRunChanged Change = new(Guid.NewGuid(), "Awaiting", "gate", "human_approval");

    private readonly RecordingHandler _handler = new();
    private readonly ServiceProvider _provider = new ServiceCollection().AddZeroAllocFlux().BuildServiceProvider();
    private readonly IStore<HomeState> _store;
    private readonly IDispatcher _dispatcher;
    private readonly HomeEffects _effects;
    private readonly RecordingLogger<HomeEffects> _logger = new();

    public HomeEffectsTests()
    {
        _store = _provider.GetRequiredService<IStore<HomeState>>();
        _dispatcher = _provider.GetRequiredService<IDispatcher>();
        _effects = new HomeEffects(_handler.Client(), _dispatcher, _store, new FakeTimeProvider(Now), _logger);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _handler.Dispose();
    }

    /// <summary>
    ///     Home reads each card's own list once, and each card keeps what it read: the pull requests card only the runs
    ///     with a pull request, the others the page as it came, with More from the cursor and the time it was read.
    ///     Red: LoadAsync reads nothing; no request is made.
    ///     Red: read the failed card with RunningQuery; the failed card's path is never requested.
    ///     Red: keep every run on the pull requests card; it holds the run with no pull request.
    ///     Red: set More from nothing; the running card's "+" is lost.
    ///     Red: stamp ReadAt with DateTimeOffset.MinValue; the wait is measured from the start of time.
    ///     Red: drop the HomeOpened dispatch; Home is not open, so every answer is dropped.
    /// </summary>
    [Fact]
    public async Task Home_reads_the_four_cards_and_each_keeps_its_own()
    {
        var withPr = RunViews.Item(Guid.NewGuid()) with { PrUrl = new Uri("https://github.com/acme/widgets/pull/1") };
        RouteAll();
        _handler.OnJson(RunningPath, RunViews.Json(new WorkflowRunList([RunViews.Item(Guid.NewGuid())], "more")))
            .OnJson(PullRequestsPath, RunViews.Json(new WorkflowRunList([RunViews.Item(Guid.NewGuid()), withPr], null)));

        await _effects.LoadAsync(canView: true, CancellationToken.None);

        _handler.Paths.Should().BeEquivalentTo([AwaitingPath, RunningPath, FailedPath, PullRequestsPath]);
        var state = _store.Value;
        state.Open.Should().BeTrue();
        state.Awaiting.Rows.Should().BeEmpty();
        state.Running.Rows.Should().ContainSingle();
        state.Running.More.Should().BeTrue();
        state.PullRequests.Rows.Should().ContainSingle().Which.Id.Should().Be(withPr.Id);
        state.ReadAt.Should().Be(Now);
    }

    /// <summary>
    ///     Someone who may not view runs gets no read at all, and Home is not open, so a run change re-fetches nothing.
    ///     Red: drop the canView check from LoadAsync; the four lists are requested.
    ///     Red: dispatch HomeOpened in place of HomeClosed for someone who may not view runs; Home is open.
    /// </summary>
    [Fact]
    public async Task Someone_who_may_not_view_runs_gets_no_read()
    {
        RouteAll();

        await _effects.LoadAsync(canView: false, CancellationToken.None);
        await ChangeAsync();

        _handler.Requests.Should().BeEmpty();
        _store.Value.Open.Should().BeFalse();
    }

    /// <summary>
    ///     Losing the role closes Home: the cards go, and a run change re-fetches nothing.
    ///     Red: LoadAsync(false) dispatches nothing; the cards stay and the change re-fetches them.
    /// </summary>
    [Fact]
    public async Task Losing_the_role_closes_home()
    {
        RouteAll();
        await _effects.LoadAsync(canView: true, CancellationToken.None);
        _handler.Requests.Clear();

        await _effects.LoadAsync(canView: false, CancellationToken.None);
        await ChangeAsync();

        _store.Value.Awaiting.Rows.Should().BeNull();
        _handler.Requests.Should().BeEmpty();
    }

    /// <summary>
    ///     A card whose read fails says why; the failure is logged as a warning with its text. That the other cards
    ///     still load is pinned by the browser test A_card_that_cannot_load_says_why_and_retries_on_its_own.
    ///     Red: dispatch nothing on a failure; the failed card has no error.
    ///     Red: log only a background read's failure; no warning is written.
    /// </summary>
    [Fact]
    public async Task A_failed_card_says_why_and_logs_a_warning()
    {
        RouteAll();
        _handler.OnJson(FailedPath, """{"detail":"The run store is down."}""", HttpStatusCode.InternalServerError);

        await _effects.LoadAsync(canView: true, CancellationToken.None);

        _store.Value.Failed.Error.Should().Be("The run store is down.");
        _logger.Entries.Should().ContainSingle().Which.Should().Match<(LogLevel Level, string Message)>(
            e => e.Level == LogLevel.Warning && e.Message.Contains("The run store is down.", StringComparison.Ordinal));
    }

    /// <summary>
    ///     A read whose token cannot be refreshed stores no error and logs nothing, because the browser is already on its
    ///     way to sign in; the card keeps its skeleton, as it always did, since no answer is stored.
    ///     Red: dispatch HomeCardLoadFailed for SigningInAgain too; the error is the signing-in text.
    ///     Red: log before the SigningInAgain check; the renewal writes a warning.
    /// </summary>
    [Fact]
    public async Task Signing_in_again_stores_no_error()
    {
        RouteAll();
        var navigation = new RecordingNavigation();
        _handler.On(AwaitingPath, HttpResponseMessage () => throw new AccessTokenNotAvailableException(navigation, navigation.RefreshFailed(), scopes: null));

        await _effects.LoadAsync(canView: true, CancellationToken.None);

        _store.Value.Awaiting.Error.Should().BeNull("a session being renewed is not an error to show");
        _logger.Entries.Should().BeEmpty("a session being renewed is not a failure");
    }

    /// <summary>
    ///     A read cancelled because Home closed stores no error and logs nothing.
    ///     Red: drop the cancellation check; the error is "Request was cancelled", and a warning is written.
    /// </summary>
    [Fact]
    public async Task A_read_cancelled_by_home_closing_stores_no_error()
    {
        using var closing = new CancellationTokenSource();
        RouteAll();
        _handler.On(AwaitingPath, async () =>
        {
            await closing.CancelAsync();
            throw new OperationCanceledException(closing.Token);
        });

        await _effects.LoadAsync(canView: true, closing.Token);

        _store.Value.Awaiting.Error.Should().BeNull();
        _logger.Entries.Should().BeEmpty("Home closing is not a failure");
    }

    /// <summary>
    ///     A run change re-fetches the awaiting, running and failed cards, not the pull requests card, and the new rows
    ///     land; while the re-fetch is in flight the old rows stay on show, so the card never turns into its skeleton.
    ///     Red: OnRunChanged fetches nothing; no request is made.
    ///     Red: re-fetch every card, HomeState.Live with PullRequests; the pull requests list is requested.
    ///     Red: re-fetch as a foreground read; the awaiting rows are null while the re-fetch is in flight.
    ///     Red: drop the HomeCardLoaded dispatch for a background read; the new gate never shows.
    /// </summary>
    [Fact]
    public async Task A_run_change_re_fetches_the_live_cards_and_keeps_their_rows_meanwhile()
    {
        RouteAll();
        await _effects.LoadAsync(canView: true, CancellationToken.None);
        _handler.Requests.Clear();
        var gate = RunViews.Item(Change.Id, "Awaiting", "human_approval");
        IReadOnlyList<WorkflowRunListItem>? inFlight = null;
        _handler.On(AwaitingPath, () =>
        {
            inFlight = _store.Value.Awaiting.Rows;
            return Task.FromResult(RecordingHandler.Json(HttpStatusCode.OK, RunViews.Json(new WorkflowRunList([gate], null))));
        });

        await ChangeAsync();

        _handler.Paths.Should().BeEquivalentTo([AwaitingPath, RunningPath, FailedPath]);
        inFlight.Should().NotBeNull("a background re-fetch keeps the rows on show");
        _store.Value.Awaiting.Rows.Should().ContainSingle().Which.Id.Should().Be(Change.Id);
    }

    /// <summary>
    ///     A card in error keeps its alert until Retry: a run change re-fetches only cards that loaded.
    ///     Red: re-fetch every live card whatever it holds; the failed list is requested again and its alert goes.
    ///     Red: OnRunChanged fetches nothing; the awaiting list is not requested.
    /// </summary>
    [Fact]
    public async Task A_card_in_error_is_not_refetched_by_a_run_change()
    {
        RouteAll();
        _handler.OnJson(FailedPath, """{"detail":"The run store is down."}""", HttpStatusCode.InternalServerError);
        await _effects.LoadAsync(canView: true, CancellationToken.None);
        _handler.OnJson(FailedPath, RunViews.Json(new WorkflowRunList([], null)));
        _handler.Requests.Clear();

        await ChangeAsync();

        _handler.Paths.Should().NotContain(FailedPath);
        _handler.Paths.Should().Contain(AwaitingPath, "the cards that loaded are re-fetched");
        _store.Value.Failed.Error.Should().Be("The run store is down.");
    }

    /// <summary>
    ///     Retry reads a card in error again, showing its skeleton meanwhile, not the old alert, and the rows land.
    ///     Red: RetryAsync reads nothing; the error stays.
    ///     Red: Retry as a background read; the old alert stays on show while the read is in flight.
    /// </summary>
    [Fact]
    public async Task Retry_reads_a_card_in_error_again()
    {
        RouteAll();
        _handler.OnJson(FailedPath, """{"detail":"The run store is down."}""", HttpStatusCode.InternalServerError);
        await _effects.LoadAsync(canView: true, CancellationToken.None);
        HomeCard? inFlight = null;
        _handler.On(FailedPath, () =>
        {
            inFlight = _store.Value.Failed;
            return Task.FromResult(RecordingHandler.Json(
                HttpStatusCode.OK, RunViews.Json(new WorkflowRunList([RunViews.Item(Guid.NewGuid(), "Failed")], null))));
        });

        await _effects.RetryAsync(HomeCardKind.Failed, CancellationToken.None);

        inFlight.Should().NotBeNull();
        inFlight!.Value.Error.Should().BeNull("Retry shows the skeleton, not the old alert");
        _store.Value.Failed.Error.Should().BeNull();
        _store.Value.Failed.Rows.Should().ContainSingle();
    }

    /// <summary>
    ///     No re-fetch runs after Home closed, for a change or a reconnect: the close clears every card, and only a card
    ///     that loaded is re-fetched.
    ///     Red: HomeClosed returns the state unchanged; the loaded cards are requested after the close.
    /// </summary>
    [Fact]
    public async Task No_refetch_runs_after_home_closed()
    {
        RouteAll();
        await _effects.LoadAsync(canView: true, CancellationToken.None);
        await _dispatcher.DispatchAsync(new HomeClosed());
        _handler.Requests.Clear();

        await ChangeAsync();
        await RefreshAsync();

        _handler.Requests.Should().BeEmpty();
    }

    /// <summary>
    ///     Asks that arrive while a re-fetch is in flight are coalesced into one more re-fetch after it, not one each.
    ///     This models concurrent callers, such as a reconnect's refresh landing beside changes; the hub's own changes,
    ///     which it delivers one at a time, are pinned by
    ///     RunHubConnectionTests.Changes_sent_while_homes_re_fetch_is_in_flight_make_one_more. Three asks during the
    ///     first make two re-fetches in all, and the second still runs, since a change may have landed after the first
    ///     one's read.
    ///     Red: drop the coalescing; each ask re-fetches, five awaiting reads in all.
    ///     Red: drop asks during a re-fetch instead of re-fetching once after it; one awaiting read in all.
    /// </summary>
    [Fact]
    public async Task Changes_during_a_refetch_are_coalesced_into_one_more()
    {
        RouteAll();
        await _effects.LoadAsync(canView: true, CancellationToken.None);
        _handler.Requests.Clear();
        var release = new TaskCompletionSource();
        var reads = 0;
        _handler.On(AwaitingPath, async () =>
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                await release.Task;
            }

            return RecordingHandler.Json(HttpStatusCode.OK, RunViews.Json(new WorkflowRunList([], null)));
        });

        _effects.OnRunChanged(Change);
        var first = _effects.Refetching;
        _effects.Refresh();
        _effects.OnRunChanged(Change);
        _effects.OnRunChanged(Change);
        release.SetResult();
        await first;
        await _effects.Refetching;

        Volatile.Read(ref reads).Should().Be(2);
    }

    /// <summary>
    ///     A reconnect re-fetches the live cards that loaded, as a change does, since changes sent while disconnected are
    ///     lost.
    ///     Red: Refresh fetches nothing; no request is made.
    /// </summary>
    [Fact]
    public async Task A_reconnect_re_fetches_the_live_cards()
    {
        RouteAll();
        await _effects.LoadAsync(canView: true, CancellationToken.None);
        _handler.Requests.Clear();

        await RefreshAsync();

        _handler.Paths.Should().BeEquivalentTo([AwaitingPath, RunningPath, FailedPath]);
    }

    /// <summary>
    ///     A background re-fetch that fails keeps the card's last rows and shows nothing, as a background refresh shows
    ///     nothing elsewhere; it logs a warning, and the card stays loaded, so the next change re-fetches it and heals it.
    ///     Red: a background failure dispatches HomeCardLoadFailed; the running card's error is "Gone." and its rows go.
    ///     Red: log only a foreground read's failure; no warning is written.
    ///     Red: end the loop with the count left at 1, not reset to 0; the next change starts no re-fetch, and the healed
    ///     rows never land.
    /// </summary>
    [Fact]
    public async Task A_failed_refetch_keeps_the_rows_and_logs_a_warning()
    {
        RouteAll();
        _handler.OnJson(RunningPath, RunViews.Json(new WorkflowRunList([RunViews.Item(Guid.NewGuid())], null)));
        await _effects.LoadAsync(canView: true, CancellationToken.None);
        _handler.OnJson(RunningPath, """{"detail":"Gone."}""", HttpStatusCode.InternalServerError);

        await ChangeAsync();

        _store.Value.Running.Error.Should().BeNull("a background refresh shows no alert");
        _store.Value.Running.Rows.Should().ContainSingle("the card keeps its last rows");
        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning);

        var healed = RunViews.Item(Guid.NewGuid());
        _handler.OnJson(RunningPath, RunViews.Json(new WorkflowRunList([healed], null)));
        await ChangeAsync();
        _store.Value.Running.Rows.Should().ContainSingle().Which.Id.Should().Be(healed.Id, "the next change heals the card");
    }

    /// <summary>
    ///     A re-fetch that faults, here a handler that throws what ApiClient does not catch, ends with a logged warning,
    ///     and the next change still re-fetches: the loop's count is reset, so live Home does not stop for good.
    ///     Red: drop the reset in RefetchLoopAsync's finally; the next change finds the count above 0 and reads nothing.
    ///     Red: drop the LogRefetchFaulted call; no warning names the fault.
    /// </summary>
    [Fact]
    public async Task A_faulted_refetch_does_not_stop_live_home()
    {
        RouteAll();
        await _effects.LoadAsync(canView: true, CancellationToken.None);
        _handler.On(AwaitingPath, HttpResponseMessage () => throw new InvalidOperationException("Not caught by ApiClient."));

        await ChangeAsync();
        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning && e.Message.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));

        RouteAll();
        _handler.Requests.Clear();
        await ChangeAsync();
        _handler.Paths.Should().Contain(AwaitingPath, "the next change still re-fetches");
    }

    /// <summary>
    ///     A change that arrives while a round faults is not lost with the reset: it starts one new loop, which reads
    ///     again. The first awaiting read is held until the change has arrived, then throws what ApiClient does not
    ///     catch.
    ///     Red: reset the count in RefetchLoopAsync's finally without starting a loop for the pending asks; one awaiting
    ///     read in all.
    /// </summary>
    [Fact]
    public async Task A_change_during_a_faulted_refetch_is_not_lost()
    {
        RouteAll();
        await _effects.LoadAsync(canView: true, CancellationToken.None);
        var release = new TaskCompletionSource();
        var reads = 0;
        _handler.On(AwaitingPath, async () =>
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                await release.Task;
                throw new InvalidOperationException("Not caught by ApiClient.");
            }

            return RecordingHandler.Json(HttpStatusCode.OK, RunViews.Json(new WorkflowRunList([], null)));
        });

        _effects.OnRunChanged(Change);
        var faulted = _effects.Refetching;
        _effects.OnRunChanged(Change);
        release.SetResult();
        await faulted;
        await _effects.Refetching;

        Volatile.Read(ref reads).Should().Be(2, "the change that arrived during the faulted round re-fetches");
    }

    /// <summary>Sends one change and waits for the re-fetch it asked for.</summary>
    private Task ChangeAsync()
    {
        _effects.OnRunChanged(Change);
        return _effects.Refetching;
    }

    /// <summary>Sends one reconnect refresh and waits for the re-fetch it asked for.</summary>
    private Task RefreshAsync()
    {
        _effects.Refresh();
        return _effects.Refetching;
    }

    /// <summary>Answers each of the four lists with an empty page.</summary>
    private void RouteAll()
    {
        var empty = RunViews.Json(new WorkflowRunList([], null));
        _handler.OnJson(AwaitingPath, empty).OnJson(RunningPath, empty).OnJson(FailedPath, empty).OnJson(PullRequestsPath, empty);
    }
}
