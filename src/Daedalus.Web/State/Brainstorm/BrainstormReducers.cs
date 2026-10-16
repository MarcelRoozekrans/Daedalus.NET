using ZeroAlloc.Flux;

namespace Daedalus.Web.State.Brainstorm;

/// <summary>
///     The Brainstorm reducers. Pure: no I/O, no clock. The session keeps an answer only to its newest read or advance;
///     see <see cref="BrainstormState"/>.
/// </summary>
public static class BrainstormReducers
{
    /// <summary>
    ///     Marks the newest read and keeps the session shown, so the messages stay while the page reloads them after a
    ///     send. A start seen out of order never moves the sequence back.
    /// </summary>
    [Reducer]
    public static BrainstormState On(BrainstormState s, BrainstormReadStarted a) =>
        s with { Sequence = Math.Max(s.Sequence, a.Sequence) };

    [Reducer]
    public static BrainstormState On(BrainstormState s, BrainstormLoaded a) =>
        a.Sequence == s.Sequence ? s with { Session = a.Session } : s;

    /// <summary>Clears the session and keeps the sequence; see <see cref="BrainstormState"/>.</summary>
    [Reducer]
    public static BrainstormState On(BrainstormState s, BrainstormClosed a) => BrainstormState.Empty with { Sequence = s.Sequence };
}
