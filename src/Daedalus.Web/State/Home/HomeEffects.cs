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
///     page went away store no error. A failed background re-fetch stores none either: the card keeps its rows. Every
///     other failure, and every failed background re-fetch, is logged as a warning, with the error text only.
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
    ///     Asks for a re-fetch of the live cards that have loaded, since any run may have entered or left one of them, and
    ///     returns at once. The change itself is only a signal: it carries no row. The hub runs its handlers one at a
    ///     time and waits for each, so a handler that waited for the re-fetch would hold back every change behind it, and
    ///     none could ever be coalesced; see <see cref="Refetching"/>.
    /// </summary>
    public void OnRunChanged(WorkflowRunChanged change) => AskForRefetch();

    /// <summary>
    ///     Asks for a re-fetch of the live cards that have loaded after a reconnect, because changes sent while
    ///     disconnected are lost, and returns at once.
    /// </summary>
    public void Refresh() => AskForRefetch();

    /// <summary>
    ///     The re-fetch loop in flight, or the last one, which has ended. It never faults: a fault is logged and ends the
    ///     loop. A test seam, reached through <c>InternalsVisibleTo</c> from <c>Daedalus.Tests.Unit</c>.
    /// </summary>
    internal Task Refetching { get; private set; } = Task.CompletedTask;

    /// <summary>
    ///     How many re-fetches were asked for since the loop in flight began, 0 when none is in flight. A test seam, as
    ///     <see cref="Refetching"/> is.
    /// </summary>
    internal int RefetchesAsked => Volatile.Read(ref _refetchesAsked);

    /// <summary>
    ///     Starts the re-fetch loop unless one is in flight: then it asks that one to run once more when it ends, since a
    ///     change may have landed after its reads. However many asks arrive meanwhile, they make one more round, not one
    ///     each.
    /// </summary>
    private void AskForRefetch()
    {
        if (Interlocked.Increment(ref _refetchesAsked) == 1)
        {
            Refetching = RefetchLoopAsync();
        }
    }

    /// <summary>
    ///     Re-fetches the live cards that have loaded, round after round, until no ask arrived during a round. A fault
    ///     ends the loop with the count reset, so the next ask starts a new one instead of waiting forever on this one.
    /// </summary>
    private async Task RefetchLoopAsync()
    {
        var ended = false;
        try
        {
            do
            {
                // Everything asked for so far is served by this round.
                Interlocked.Exchange(ref _refetchesAsked, 1);
                // A closed Home has no card loaded, so nothing is re-fetched after HomeClosed.
                var state = home.Value;
                await Task.WhenAll(HomeState.Live.Where(kind => state.Card(kind).Loaded)
                    .Select(kind => LoadCardAsync(kind, true, CancellationToken.None)));
            }
            while (Interlocked.CompareExchange(ref _refetchesAsked, 0, 1) != 1);

            ended = true;
        }
        catch (Exception exception)
        {
            // Every fault, not a list of the expected ones: nothing awaits this loop, so an escaped one would be lost.
            LogRefetchFaulted(logger, exception.GetType().Name);
        }
        finally
        {
            if (!ended)
            {
                Interlocked.Exchange(ref _refetchesAsked, 0);
            }
        }
    }

    /// <summary>
    ///     Reads one card. A failure of a read that shows the skeleton, on open or Retry, is the card's error. A
    ///     <paramref name="background"/> read's failure is logged and stores nothing: the card keeps its last rows and stays
    ///     loaded, so the next change heals it, and a refresh no one asked for never turns into an alert. A
    ///     <see cref="SessionExpiry.SigningInAgain"/> failure, or one after <paramref name="ct"/> was cancelled, stores
    ///     nothing and logs nothing.
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
            if (!background)
            {
                await dispatcher.DispatchAsync(new HomeCardLoadFailed(card, sequence, result.Error));
            }
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

    [LoggerMessage(EventId = 3301, Level = LogLevel.Warning, Message = "Home's live re-fetch faulted: {ExceptionType}")]
    private static partial void LogRefetchFaulted(ILogger logger, string exceptionType);
}
