using Daedalus.Application.DTOs.WorkflowRuns;
using Daedalus.Web.Auth;
using Daedalus.Web.State.Runs;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Flux;

namespace Daedalus.Web.State.Home;

/// <summary>
///     Home's I/O: each card reads one bounded page of the run list and fails on its own; see <see cref="NeedsMe"/> for
///     what each reads. A run change or a reconnect re-fetches the cards in <see cref="HomeState.Live"/> that have loaded,
///     in the background, while Home is open; changes that arrive while a re-fetch is in flight are coalesced into one
///     more after it. An expected failure is an action, never an exception. A session being renewed and a read whose
///     page went away store no error. A failure that is stored is logged as a warning, with the error text only.
/// </summary>
public sealed partial class HomeEffects(
    ApiClient api, IDispatcher dispatcher, IStore<HomeState> home, TimeProvider time, ILogger<HomeEffects> logger)
{
    /// <summary>
    ///     How many re-fetches were asked for since the one in flight began: 0 when none is in flight. Read and written
    ///     only through <see cref="Interlocked"/>.
    /// </summary>
    private int _refetchesAsked;

    /// <summary>
    ///     Opens Home and reads the four cards when <paramref name="canView"/>, showing each card's skeleton meanwhile.
    ///     Otherwise, for someone who may not view runs, makes no read and closes Home, so a run change re-fetches nothing.
    /// </summary>
    public async Task LoadAsync(bool canView, CancellationToken ct)
    {
        if (!canView)
        {
            await dispatcher.DispatchAsync(new HomeClosed());
            return;
        }

        await dispatcher.DispatchAsync(new HomeOpened());
        await Task.WhenAll(
            LoadCardAsync(HomeCardKind.Awaiting, false, ct),
            LoadCardAsync(HomeCardKind.Running, false, ct),
            LoadCardAsync(HomeCardKind.Failed, false, ct),
            LoadCardAsync(HomeCardKind.PullRequests, false, ct));
    }

    /// <summary>Reads <paramref name="card"/> again, showing its skeleton meanwhile; the Retry of a card in error.</summary>
    public Task RetryAsync(HomeCardKind card, CancellationToken ct) => LoadCardAsync(card, false, ct);

    /// <summary>
    ///     Re-fetches the live cards that have loaded, since any run may have entered or left one of them. The change
    ///     itself is only a signal: it carries no row.
    /// </summary>
    public Task OnRunChangedAsync(WorkflowRunChanged change, CancellationToken ct) => RefetchAsync(ct);

    /// <summary>Re-fetches the live cards that have loaded after a reconnect, because changes sent while disconnected are lost.</summary>
    public Task RefreshAsync(CancellationToken ct) => RefetchAsync(ct);

    /// <summary>
    ///     Re-fetches the live cards that have loaded, unless one is in flight: then it asks that one to run once more
    ///     when it ends, since a change may have landed after its reads. However many changes arrive meanwhile, they make
    ///     one more re-fetch, not one each.
    /// </summary>
    private async Task RefetchAsync(CancellationToken ct)
    {
        if (Interlocked.Increment(ref _refetchesAsked) > 1)
        {
            return;
        }

        do
        {
            // Everything asked for so far is served by this round.
            Interlocked.Exchange(ref _refetchesAsked, 1);
            var state = home.Value;
            if (!state.Open)
            {
                continue;
            }

            await Task.WhenAll(HomeState.Live.Where(kind => state.Card(kind).Loaded).Select(kind => LoadCardAsync(kind, true, ct)));
        }
        while (Interlocked.CompareExchange(ref _refetchesAsked, 0, 1) != 1);
    }

    /// <summary>
    ///     Reads one card. A failure is the card's error; a <see cref="SessionExpiry.SigningInAgain"/> failure, or one after
    ///     <paramref name="ct"/> was cancelled, stores nothing.
    /// </summary>
    private async Task LoadCardAsync(HomeCardKind card, bool background, CancellationToken ct)
    {
        // Read and dispatched with no await between: the dispatch reduces at once, on the one browser thread, so the
        // next read takes the number after this one.
        var sequence = home.Value.Sequence + 1;
        await dispatcher.DispatchAsync(new HomeCardLoadStarted(card, sequence, background));
        var result = await api.GetRunsAsync(Query(card, time.GetUtcNow()), ct);
        if (result.IsSuccess)
        {
            var rows = card == HomeCardKind.PullRequests ? NeedsMe.RecentPullRequests(result.Value.Runs) : result.Value.Runs;
            await dispatcher.DispatchAsync(new HomeCardLoaded(card, sequence, rows, result.Value.Next is not null, time.GetUtcNow()));
        }
        else if (!SessionExpiry.IsSigningIn(result.Error) && !ct.IsCancellationRequested)
        {
            LogLoadFailed(logger, card, result.Error);
            await dispatcher.DispatchAsync(new HomeCardLoadFailed(card, sequence, result.Error));
        }
    }

    /// <summary>The read of <paramref name="card"/> at <paramref name="now"/>.</summary>
    private static RunListQuery Query(HomeCardKind card, DateTimeOffset now) => card switch
    {
        HomeCardKind.Awaiting => NeedsMe.AwaitingQuery,
        HomeCardKind.Running => NeedsMe.RunningQuery,
        HomeCardKind.Failed => NeedsMe.FailedQuery(now),
        _ => NeedsMe.PullRequestQuery,
    };

    [LoggerMessage(EventId = 3300, Level = LogLevel.Warning, Message = "Home's {Card} card could not be loaded: {Error}")]
    private static partial void LogLoadFailed(ILogger logger, HomeCardKind card, string error);
}
