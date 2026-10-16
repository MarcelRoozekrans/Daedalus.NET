using ZeroAlloc.Flux;

namespace Daedalus.Web.State.Home;

/// <summary>
///     Home's reducers. Pure: no I/O, no clock. A card keeps an answer only to its newest read, and only while Home is
///     open; see <see cref="HomeState"/>.
/// </summary>
public static class HomeReducers
{
    [Reducer]
    public static HomeState On(HomeState s, HomeOpened a) => s with { Open = true };

    /// <summary>
    ///     Takes the read's number whether or not Home is open, so a number is never handed out twice; the card waits for
    ///     the read only while Home is open. A read that is not a background one shows the card's skeleton; a background
    ///     one keeps the rows on show until its answer lands.
    /// </summary>
    [Reducer]
    public static HomeState On(HomeState s, HomeCardLoadStarted a)
    {
        var numbered = s with { Sequence = Math.Max(s.Sequence, a.Sequence) };
        if (!s.Open)
        {
            return numbered;
        }

        var card = s.Card(a.Card);
        return numbered.With(a.Card, a.Background
            ? card with { Sequence = a.Sequence }
            : new HomeCard(null, null, false, a.Sequence));
    }

    [Reducer]
    public static HomeState On(HomeState s, HomeCardLoaded a) =>
        Awaited(s, a.Card, a.Sequence)
            ? s.With(a.Card, new HomeCard(a.Rows, null, a.More, a.Sequence)) with { ReadAt = a.ReadAt }
            : s;

    /// <summary>A failure clears the rows, so the heading carries no count beside the alert.</summary>
    [Reducer]
    public static HomeState On(HomeState s, HomeCardLoadFailed a) =>
        Awaited(s, a.Card, a.Sequence) ? s.With(a.Card, new HomeCard(null, a.Error, false, a.Sequence)) : s;

    /// <summary>Clears every card and keeps the sequence; see <see cref="HomeState"/>.</summary>
    [Reducer]
    public static HomeState On(HomeState s, HomeClosed a) => HomeState.Closed with { Sequence = s.Sequence };

    /// <summary>Whether Home is open and read <paramref name="sequence"/> is the newest of <paramref name="card"/>.</summary>
    private static bool Awaited(HomeState s, HomeCardKind card, long sequence) => s.Open && s.Card(card).Sequence == sequence;
}
