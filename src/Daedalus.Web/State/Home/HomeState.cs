using Daedalus.Application.DTOs.WorkflowRuns;
using ZeroAlloc.Flux;

namespace Daedalus.Web.State.Home;

/// <summary>Home's four cards, in order of urgency.</summary>
public enum HomeCardKind
{
    /// <summary>Runs parked at the human gate.</summary>
    Awaiting,

    /// <summary>Runs running.</summary>
    Running,

    /// <summary>Runs that failed in the last seven days.</summary>
    Failed,

    /// <summary>Recent pull requests of manufacture runs.</summary>
    PullRequests,
}

/// <summary>One of Home's cards: what it shows and which read it waits for.</summary>
/// <param name="Rows">The rows, or <see langword="null"/> while a read that shows the skeleton is in flight, before any, and after a failure.</param>
/// <param name="Error">
///     Why the last read failed, or <see langword="null"/>. Never <see cref="Auth.SessionExpiry.SigningInAgain"/>, and never a
///     cancellation: neither is stored.
/// </param>
/// <param name="More">Whether the list holds more than the page the card read, so its count is a lower bound.</param>
/// <param name="Sequence">The number of the card's newest read; an answer to any other is dropped.</param>
public readonly record struct HomeCard(IReadOnlyList<WorkflowRunListItem>? Rows, string? Error, bool More, long Sequence)
{
    /// <summary>A card with nothing read.</summary>
    public static HomeCard Empty { get; } = new(null, null, false, 0);

    /// <summary>
    ///     Whether the card shows rows: it has loaded and is not in error, since a failure clears the rows. Only such a
    ///     card is re-fetched by a run change or a reconnect; a card in error keeps its alert until Retry.
    /// </summary>
    public bool Loaded => Rows is not null;
}

/// <summary>
///     Home's "what needs me" panel: its four cards and when they were last read.
///     <para>
///         Lifetime: page-only, cleared by <see cref="HomeClosed"/> when the panel is disposed or shown to someone who may
///         not view runs, so a run change or a reconnect re-fetches nothing while Home is off screen.
///         <paramref name="Sequence"/> survives the clear, so an answer to a read made before it never matches a card read
///         after it.
///     </para>
/// </summary>
/// <param name="Awaiting">The awaiting approval card.</param>
/// <param name="Running">The running card.</param>
/// <param name="Failed">The failed in the last 7 days card.</param>
/// <param name="PullRequests">The recent pull requests card.</param>
/// <param name="ReadAt">When a card last arrived, for how long a run has waited.</param>
/// <param name="Open">Whether Home is open for someone who may view runs; a card's read is kept only then.</param>
/// <param name="Sequence">The number of the newest read started on any card, never reset.</param>
[Feature(InitialState = nameof(Initial))]
public readonly partial record struct HomeState(
    HomeCard Awaiting, HomeCard Running, HomeCard Failed, HomeCard PullRequests, DateTimeOffset ReadAt, bool Open, long Sequence)
{
    /// <summary>The cards a run change or a reconnect re-fetches. The pull requests card is read only when Home opens or on Retry.</summary>
    public static IReadOnlyList<HomeCardKind> Live { get; } = [HomeCardKind.Awaiting, HomeCardKind.Running, HomeCardKind.Failed];

    /// <summary>The state with Home closed and nothing read.</summary>
    public static HomeState Closed { get; } =
        new(HomeCard.Empty, HomeCard.Empty, HomeCard.Empty, HomeCard.Empty, DateTimeOffset.MinValue, false, 0);

    /// <summary>The state before Home opened.</summary>
    public static HomeState Initial(IServiceProvider services) => Closed;

    /// <summary>The card of <paramref name="kind"/>.</summary>
    public HomeCard Card(HomeCardKind kind) => kind switch
    {
        HomeCardKind.Awaiting => Awaiting,
        HomeCardKind.Running => Running,
        HomeCardKind.Failed => Failed,
        _ => PullRequests,
    };

    /// <summary>This state with the card of <paramref name="kind"/> replaced by <paramref name="card"/>.</summary>
    public HomeState With(HomeCardKind kind, HomeCard card) => kind switch
    {
        HomeCardKind.Awaiting => this with { Awaiting = card },
        HomeCardKind.Running => this with { Running = card },
        HomeCardKind.Failed => this with { Failed = card },
        _ => this with { PullRequests = card },
    };
}
