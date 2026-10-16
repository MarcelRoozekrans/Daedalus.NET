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
    ///     Red: stamp ReadAt before the read with DateTimeOffset.MinValue; the wait is measured from the start of time.
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
    ///     Red: dispatch HomeOpened before the canView check; Home is open for someone who may not see runs.
    /// </summary>
    [Fact]
    public async Task Someone_who_may_not_view_runs_gets_no_read()
    {
        RouteAll();

        await _effects.LoadAsync(canView: false, CancellationToken.None);
        await _effects.OnRunChangedAsync(Change, CancellationToken.None);

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
        await _effects.OnRunChangedAsync(Change, CancellationToken.None);

        _store.Value.Awaiting.Rows.Should().BeNull();
        _handler.Requests.Should().BeEmpty();
    }

    /// <summary>
    ///     A card whose read fails says why, and only that card fails; the failure is logged as a warning with its text.
    ///     Red: dispatch nothing on a failure; the failed card has no error.
    ///     Red: drop the LogLoadFailed call; no warning is written.
    /// </summary>
    [Fact]
    public async Task A_failed_card_says_why_and_only_that_card_fails()
    {
        RouteAll();
        _handler.OnJson(FailedPath, """{"detail":"The run store is down."}""", HttpStatusCode.InternalServerError);

        await _effects.LoadAsync(canView: true, CancellationToken.None);

        _store.Value.Failed.Error.Should().Be("The run store is down.");
        _store.Value.Awaiting.Rows.Should().NotBeNull("the other cards still load");
        _logger.Entries.Should().ContainSingle().Which.Should().Match<(LogLevel Level, string Message)>(
            e => e.Level == LogLevel.Warning && e.Message.Contains("The run store is down.", StringComparison.Ordinal));
    }

    /// <summary>
    ///     A read whose token cannot be refreshed stores no error and logs nothing, because the browser is already on its
    ///     way to sign in; the card keeps its skeleton, as it always did.
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
        _store.Value.Awaiting.Rows.Should().BeNull();
        _logger.Entries.Should().BeEmpty("a session being renewed is not a failure");
    }

    /// <summary>
    ///     A read cancelled because Home closed stores no error and logs nothing.
    ///     Red: drop the cancellation check; the error is "Request was cancelled".
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
    ///     Red: OnRunChangedAsync fetches nothing; no request is made.
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

        await _effects.OnRunChangedAsync(Change, CancellationToken.None);

        _handler.Paths.Should().BeEquivalentTo([AwaitingPath, RunningPath, FailedPath]);
        inFlight.Should().NotBeNull("a background re-fetch keeps the rows on show");
        _store.Value.Awaiting.Rows.Should().ContainSingle().Which.Id.Should().Be(Change.Id);
    }

    /// <summary>
    ///     A card in error keeps its alert until Retry: a run change re-fetches only cards that loaded.
    ///     Red: re-fetch every live card whatever it holds; the failed list is requested again and its alert goes.
    /// </summary>
    [Fact]
    public async Task A_card_in_error_is_not_refetched_by_a_run_change()
    {
        RouteAll();
        _handler.OnJson(FailedPath, """{"detail":"The run store is down."}""", HttpStatusCode.InternalServerError);
        await _effects.LoadAsync(canView: true, CancellationToken.None);
        _handler.OnJson(FailedPath, RunViews.Json(new WorkflowRunList([], null)));
        _handler.Requests.Clear();

        await _effects.OnRunChangedAsync(Change, CancellationToken.None);

        _handler.Paths.Should().NotContain(FailedPath);
        _handler.Paths.Should().Contain(AwaitingPath, "the cards that loaded are re-fetched");
        _store.Value.Failed.Error.Should().Be("The run store is down.");
    }

    /// <summary>
    ///     Retry reads a card in error again, showing its skeleton meanwhile, and the rows land.
    ///     Red: RetryAsync reads nothing; the error stays.
    ///     Red: Retry as a background read; the failed card is no longer loaded, so nothing is read.
    /// </summary>
    [Fact]
    public async Task Retry_reads_a_card_in_error_again()
    {
        RouteAll();
        _handler.OnJson(FailedPath, """{"detail":"The run store is down."}""", HttpStatusCode.InternalServerError);
        await _effects.LoadAsync(canView: true, CancellationToken.None);
        _handler.OnJson(FailedPath, RunViews.Json(new WorkflowRunList([RunViews.Item(Guid.NewGuid(), "Failed")], null)));

        await _effects.RetryAsync(HomeCardKind.Failed, CancellationToken.None);

        _store.Value.Failed.Error.Should().BeNull();
        _store.Value.Failed.Rows.Should().ContainSingle();
    }

    /// <summary>
    ///     No re-fetch runs after Home closed, for a change or a reconnect.
    ///     Red: drop the Open check from the re-fetch; the live cards are requested after the close.
    ///     Red: HomeClosed leaves Open set; the live cards are requested after the close.
    /// </summary>
    [Fact]
    public async Task No_refetch_runs_after_home_closed()
    {
        RouteAll();
        await _effects.LoadAsync(canView: true, CancellationToken.None);
        await _dispatcher.DispatchAsync(new HomeClosed());
        _handler.Requests.Clear();

        await _effects.OnRunChangedAsync(Change, CancellationToken.None);
        await _effects.RefreshAsync(CancellationToken.None);

        _handler.Requests.Should().BeEmpty();
    }

    /// <summary>
    ///     Changes that arrive while a re-fetch is in flight are coalesced into one more re-fetch after it, not one each:
    ///     three changes during the first make two re-fetches in all, and the second still runs, since a change may have
    ///     landed after the first one's read.
    ///     Red: drop the coalescing; each change re-fetches, four awaiting reads in all.
    ///     Red: drop changes during a re-fetch instead of re-fetching once after it; one awaiting read in all.
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

        var first = _effects.OnRunChangedAsync(Change, CancellationToken.None);
        await _effects.OnRunChangedAsync(Change, CancellationToken.None);
        await _effects.OnRunChangedAsync(Change, CancellationToken.None);
        await _effects.OnRunChangedAsync(Change, CancellationToken.None);
        release.SetResult();
        await first;

        Volatile.Read(ref reads).Should().Be(2);
    }

    /// <summary>
    ///     A reconnect re-fetches the live cards that loaded, as a change does, since changes sent while disconnected are
    ///     lost.
    ///     Red: RefreshAsync fetches nothing; no request is made.
    /// </summary>
    [Fact]
    public async Task A_reconnect_re_fetches_the_live_cards()
    {
        RouteAll();
        await _effects.LoadAsync(canView: true, CancellationToken.None);
        _handler.Requests.Clear();

        await _effects.RefreshAsync(CancellationToken.None);

        _handler.Paths.Should().BeEquivalentTo([AwaitingPath, RunningPath, FailedPath]);
    }

    /// <summary>
    ///     A background re-fetch that fails says why on that card, as a failed read always has, and logs a warning.
    ///     Red: drop the failure dispatch for a background read; the running card keeps its stale rows silently.
    /// </summary>
    [Fact]
    public async Task A_failed_refetch_says_why()
    {
        RouteAll();
        await _effects.LoadAsync(canView: true, CancellationToken.None);
        _handler.OnJson(RunningPath, """{"detail":"Gone."}""", HttpStatusCode.InternalServerError);

        await _effects.OnRunChangedAsync(Change, CancellationToken.None);

        _store.Value.Running.Error.Should().Be("Gone.");
        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning);
    }

    /// <summary>Answers each of the four lists with an empty page.</summary>
    private void RouteAll()
    {
        var empty = RunViews.Json(new WorkflowRunList([], null));
        _handler.OnJson(AwaitingPath, empty).OnJson(RunningPath, empty).OnJson(FailedPath, empty).OnJson(PullRequestsPath, empty);
    }
}
